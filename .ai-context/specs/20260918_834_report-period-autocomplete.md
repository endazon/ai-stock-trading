---
title: /report の period に入力補完を足し、見つからないときに会話キーの形を案内する
type: spec
status: accepted
related_ids: [FR-07, FR-14, UC-03, UC-04, UC-05, ADR-0003, IADR-0062, IADR-0116, IADR-0240]
author: endazon (with Claude Code)
created: 2026-09-18
updated: 2026-09-18
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-07「報告書は利用者の確定をもって有効になる」/ FR-14「Discord からの操作」)
  - planning:projects/ai-stock-trading/06_technical/07_discord-bot-design.md (認証・認可 / 二重実行防止)
---

# 仕様書: `/report` の period 入力補完と 404 の案内（#834）

## 起点

- #834（bug・PoC 運用中に利用者が遭遇）。`/report action:approve period:2026-09-15` が 404 になり、
  報告書を確定できなかった（18:28・18:29・18:42・18:43 の 4 回。notification-service ログ）。
- 直前に修正した #835（週報キーの大文字 `W` が潰れる）と同じ経路の別の失敗である。#835 決定 3 が
  「利用者がキーを覚えなくて済むようにする件は #834 の射程」として送った分を本件で閉じる。

## 原因

1. `period` は**会話キー**（`daily-2026-09-18` / `weekly-2026-W38` / `monthly-2026-09`）であり、
   日付だけでは実在しない。`/report` の `period` は自由入力の文字列オプションで、説明文に例があるだけである
   （`DiscordNetBotGateway.OnReadyAsync` のコマンド定義）。
2. 誤入力の応答が `HttpReportReviewController.FailureMessage` の汎用文言
   （「レビュー局面の照会に失敗しました（HTTP 404）」）であり、**何が悪いのか・正しい形が何か**を伝えない。

利用者側に回復手段が無い（キーの一覧を Discord から見る方法も無い）。

## 決定

1. **`period` に Discord の入力補完（autocomplete）を付ける。** 候補は報告書サービスの一覧 API
   （`GET /reports`・OwnerOnly）から採り、**新しい順**（対象期間の開始日の降順、同日はキーの降順）に
   **最大 25 件**（Discord の上限）返す。
2. **候補に使うのは会話キーだけ**とする。一覧応答から射影するのは `periodKey` と並び替えに使う
   `periodStart` の 2 つで、**本文・要約・状態は読まない**。
   - 本文・要約: IADR-0240 決定 4（サニタイズ済みの通知経路を迂回する経路を新設しない）。
   - 状態（`state`）: IADR-0240 決定 5（enum の JSON 表現＝数値/文字列に結合しない）。**一覧に載る報告書は
     レビュー待ち（Draft）も確定済み（Confirmed）も等しくレビュー操作の対象**であり、候補を絞るのに状態は要らない。
   - `periodStart` は文字列として受けてから日付として解釈を試みる（解釈できなければ末尾へ倒す）。
     一覧が空・解釈不能でも候補なしに倒れるだけで、`/report` 自体は従来どおり動く。
3. **取得に失敗しても素通しする（fail-safe）。** 一覧 API が HTTP エラー・タイムアウト・解釈不能で
   終わったら**候補なし**（空配列）を返す。利用者は従来どおり手入力できる。**Bot は落とさない**
   ——補完は入力の補助であり、統制ではない。
4. **認可は従来どおり**。候補を返す前に `DiscordCommandAuthorizer`（多層認証）を通し、**許可外の利用者には
   候補を返さない**（kill switch / `/gfv` と同水準）。補完は「どの会話キーが存在するか」を漏らす経路であり、
   窓口の認可水準を下げない。
5. **絞り込みは利用者の入力そのもので行い、推測補正はしない**（#835 決定 3 と同じ方針）。
   前方一致を先に、部分一致を後に並べる（大小文字は無視する）。入力が空なら新しい順のまま返す。
   候補は `BotCommandParser` が受け付ける値域（`^[A-Za-z0-9-]{1,32}$`）のものに限る
   ——受け付けない値を候補に出すと、選んだ結果が `Unknown` へ倒れる。
6. **404 は「その会話キーの報告書が見つかりません（例: `daily-2026-09-18`）」と返す。**
   他の HTTP 状態の文言は変えない（401/403 の owner クライアントの手掛かりもそのまま）。
   照会・確定・差し戻しの 3 操作に等しく効く（いずれも 404 は「その会話キーが無い」を意味する）。

実装判断の記録は **IADR-0240 の決定9（入力補完）・決定10（404 の案内）**として日付つき追記で残す
（新しい IADR 番号は採らない——同じ窓口・同じ経路の決定であり、分けると読み手が両方を突き合わせる羽目になる）。

## 変更しないもの

- 報告書サービス（#14）側は**無改修**。一覧 API（`GET /reports`・OwnerOnly）は既存のまま使う。
- 多層認証・版番号ガード・二重確定の吸収（IADR-0240 決定 2・決定 3・決定 7）。
- 会話キーの解析（`BotCommandParser`。#835 の値域と大小文字の扱いをそのまま使う）。
- 確認ボタンの 2 段階確定（詳細設計07 §認証・認可 4項）。

## 影響範囲

| ファイル | 変更 |
| --- | --- |
| `Features/Notifications/IReportReviewController.cs` | 一覧照会 `ListPeriodKeysAsync` を追加（fail-safe の契約をコメントで固定） |
| `Infrastructure/ExternalServices/HttpReportReviewController.cs` | `GET /reports` の実装と 404 の案内文言 |
| `Domain/ReportPeriodSuggestions.cs`（新規） | 候補の絞り込み・並び・件数上限の純関数 |
| `Domain/BotCommandParser.cs` | 会話キーの値域判定を `IsPeriodKey` として公開（候補側と共用） |
| `Features/Notifications/ReviewReport/ReportCommandHandler.cs` | `SuggestPeriodsAsync`（認可 → 一覧 → 絞り込み） |
| `Infrastructure/ExternalServices/DiscordNetBotGateway.cs` | `period` に `WithAutocomplete(true)`・`AutocompleteExecuted` の受け口 |

## 受け入れ基準

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | 一覧 API の応答から会話キーを**新しい順**に取り出す | 単体（HTTP fake） |
| 2 | 一覧の取得に失敗（HTTP エラー・例外）したら**空**で返る（例外を投げない） | 単体（HTTP fake） |
| 3 | 候補は最大 25 件に収まる | 単体 |
| 4 | 入力で絞り込まれ、前方一致が部分一致より前に来る（大小文字は無視） | 単体 |
| 5 | **許可外の利用者には候補を返さない**（否定形） | 単体（ハンドラ） |
| 6 | 一覧の取得に失敗しても `/report show` は従来どおり動く（否定形・fail-safe） | 単体（ハンドラ） |
| 7 | 404 の応答に会話キーの例を含む案内が返る（状態番号だけにしない） | 単体（HTTP fake） |
| 8 | 404 以外の HTTP 状態の文言は従来どおり | 既存単体 |
| 9 | 値域外の会話キー（記号入り等）は候補に出さない | 単体 |

## 未検証・保留

- **実 Discord での入力補完の疎通は未検証**（`docs/blocked-tasks.md` A-7a と同じ制約。CI から Discord
  Gateway へは接続できない）。Gateway の受け口（`AutocompleteExecuted`）は Discord.Net の型に依存するため
  単体テストの対象外とし、判断（認可・絞り込み・fail-safe）はすべて Discord.Net 非依存の層へ寄せて固定する。
- 補完の候補に**状態（レビュー待ち／確定済み）を併記する**ことは見送った（IADR-0240 決定 5 の
  「enum の JSON 表現に結合しない」を崩すため）。必要になったら報告書サービス側に表現非依存の
  一覧射影を足す形で別 issue とする。
- 🟡 **一覧 API は射影もページングも持たず、本文（`Body`）・要約（`PolicySummary`）を含む全件を返す。**
  通知サービス側は `periodKey` と `periodStart` だけを射影して残りを読み捨てるため IADR-0240 決定 4/5 は
  守られるが、**転送されている応答自体は全文つき全件**である。補完は打鍵ごとに発火するため、報告書が
  貯まるほど応答サイズとレイテンシが線形に悪化する（日報は毎日増える）。**本 PR では #14 無改修の方針を
  優先して受容する**——現時点の件数（PoC 運用開始から数十件）では実害が出ておらず、軽い一覧を足すのは
  報告書サービス側の API 追加（`GET /reports?fields=...` または `/reports/keys`）になり、射程が変わる。
  **件数が増える前に別 issue で #14 側へ軽量一覧を足すのが本筋**であり、その時点で本アダプタは
  差し替えるだけで済む（射影の形は変わらない）。
