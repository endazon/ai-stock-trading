---
title: /report の入力補完（#834）の監査で残った論点 6 件のうち、補完専用の時間予算・開始日の表現依存・台帳の再測定手順・コメントの実態合わせを片付け、軽い一覧は見送って残す
type: spec
status: accepted
related_ids: [FR-14, FR-07, UC-03, UC-04, UC-05, IADR-0240]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-14「Discord からの操作」)
  - planning:projects/ai-stock-trading/06_technical/07_discord-bot-design.md (認証・認可)
---

# 仕様書: 入力補完の監査の残り 6 件（#843）

## 起点

- #843（#841 の監査で挙がった非ブロッキングの 6 件）と、同 issue の 2026-09-23 のトリアージコメント。
- 前提の仕様書: `.ai-context/specs/20260918_834_report-period-autocomplete.md`（凍結記録。本 PR では書き換えない）。
- 決定の記録: IADR-0240 決定 9 への日付つき追記（新しい IADR 番号は採らない——同じ窓口・同じ経路の決定であり、
  #834 と同じく分けると読み手が両方を突き合わせる羽目になる）。

## 🔴 実測（`origin/develop` = `08c53b91`・`git rev-parse --is-shallow-repository` → `false`）

| 事実 | 出典 |
| --- | --- |
| 名前付き HttpClient `report-review` の `Timeout` は 5 秒。Discord の autocomplete 応答期限は 3 秒 | `NotificationService/Program.cs`（`AddHttpClient("report-review", …)`） |
| 補完経路に短い予算は無い。Gateway は `SuggestPeriodsAsync` を CancellationToken 無しで呼び、ハンドラは既定の `CancellationToken.None` のまま一覧照会へ渡す | `DiscordNetBotGateway.OnAutocompleteAsync` / `ReportCommandHandler.SuggestPeriodsAsync` |
| アダプタの `Handled` は「呼び出し側のトークンが取り消されたとき」だけ例外を伝播させる。したがって**ハンドラが予算トークンを渡すと、予算切れはアダプタの外へ伝播する**（アダプタは空へ丸めない） | `HttpReportReviewController.Handled` |
| 🔴 **`periodStart` が数値（`20260918`）で来ると一覧ごと空になる。** トリアージコメントは「`StartOf` が末尾へ回すだけで一覧ごと空へは倒れない」としたが、**`string?` への逆シリアル化そのものが `JsonException` で失敗する**（`StartOf` まで届かない）。一時テストで実測: `[{"periodKey":"daily-2026-09-18","periodStart":20260918},{"periodKey":"daily-2026-09-17","periodStart":"2026-09-17"}]` → `ListPeriodKeysAsync` は**空**（2 件とも失われる）。監査の観測が正しく、トリアージの縮小の前提は成り立たない（規則 10: 他人の数えを検証せず転記しない） | 一時テスト（`dotnet test --filter PROBE`・Passed＝空を表明して通過。本 PR では削除し正式テストへ置き換えた） |
| 報告書サービスの `PeriodStart` は `DateOnly`（既定の JSON 表現は `"2026-09-18"` の文字列）。**現行の組み合わせでは数値は来ない**——数値化は報告書サービスのシリアライザ設定の変更でだけ起きる | `ReportService/Domain/TradingReport.cs` |
| Discord.Net 3.20.1 で `GuildId` を宣言しているのは基底 `SocketInteraction` だけ（派生型での `new` 再宣言は無い） | `~/.nuget/packages/discord.net.websocket/3.20.1/lib/net10.0/Discord.Net.WebSocket.xml` の `P:Discord.WebSocket.*.GuildId` は `SocketInteraction.GuildId` の 1 件だけ |
| `docs/blocked-tasks.md` A-7a の再測定手順は ①`/report` 2 回連投 ②`/gfv clear` ③第 2 アカウントの否定形 ④A-7b の注記 の 4 つで、補完の項が無い | `grep -n "補完\|autocomplete" docs/blocked-tasks.md` → 0 件 |
| 一覧 API は `GET /reports` だけで、射影・ページングを持たない。軽い射影の前例（`?fields=` やキー専用ルート）はどのサービスにも無い。`docs/api/openapi.yaml` に `/reports` は載っていない | `ReportService/Features/Reports/ListReports/Endpoint.cs` / `EfReportStore.List` / `git grep 'MapGet("'` |

## 規則 9〜11（着手前に引いた母集合）

### 規則 9: 誤りの側の文字列で全文書を走査

| 走査語 | ヒット | 扱い |
| --- | --- | --- |
| `欠けると DM 扱い` | `DiscordNetBotGateway.cs` 1 件 | 是正（項目 5） |
| `文字列で受ける` / `文字列として受けてから` | `HttpReportReviewController.cs` 2 件、`20260918_834` 仕様書 1 件、`LlmPriceTable.cs`・`20260731_303` 仕様書 各 1 件 | アダプタの 2 件を是正（項目 3）。**#834 の仕様書は凍結記録のため書き換えない**（本仕様書が後継の記録）。`LlmPriceTable` 系は別の型・別の理由（構成パッケージを持ち込まない）で無関係 |
| `別 issue の射程`（backend） | `HttpReportReviewController.cs` 1 件 | 是正（項目 1 の見送りを指す形へ） |
| `補完` / `autocomplete`（docs/） | 4 件（いずれも無関係の「補完する」の一般語） | 対象外。台帳 A-7a に補完の項を新設（項目 4） |
| `"report-review"`（backend） | `Program.cs` 3 行 | 5 秒の `Timeout` は照会・確定・差し戻しにも効くため**変えない**。補完の予算は補完経路だけに掛ける |

### 規則 10: この変更で新たに誤りになる自分の記述

- IADR-0240 決定 9 の「取得に失敗したら候補なしで素通しする」は、予算切れも含む形に広がる（追記で明記する）。
- `IReportReviewController.ListPeriodKeysAsync` の契約コメント「呼び出し側のキャンセルだけは伝播する」は変わらない。
  **予算切れはアダプタから見ると「呼び出し側のキャンセル」になる**ため伝播し、ハンドラが空へ丸める——ハンドラ側の
  コメントにこの分担を書く（アダプタ側を読むと「タイムアウトは空」と誤読し得るため）。
- テストの fake（`FakeReportReviewController.ListPeriodKeysAsync`）はトークンを見ていなかった。予算のテストのため
  トークンを尊重する模擬を足す。

### 規則 11: 窓（時間差）

補完の予算は「Discord の 3 秒の期限」という窓を扱う。**増える側**＝一覧の遅延が伸びる（件数増・報告書サービスの負荷）、
**減る側**＝一覧が速く返る（予算で正常応答を切ってはならない）。

| 形 | P1: 一覧が予算を超えて遅い（増える側） | P2: 一覧が速い（減る側） | P3: 呼び出し側の取り消し | 判定 |
| --- | --- | --- | --- | --- |
| A. 一覧照会の開始から固定 2.5 秒（**後**の端＝照会の終わりだけを見る） | 候補なしで静かに終わる（単体で実測） | 候補がそのまま返る（単体で実測） | 伝播する（単体で実測） | **採用** |
| B. Discord の着信時刻（interaction の作成時刻）から 3 秒（**前**の端だけを見る） | 期限の手前で切れる | 切れない | 伝播する | 不採用: 着信時刻は Discord 側の時計（snowflake）で、**Pod の時計とのずれ**がそのまま予算に入る。ずれが負なら P2 で正常応答を切り、正なら P1 で期限を越える。CI では時計ずれを再現できず実測できない |
| C. `min(A, B)`（両端） | 切れる | B と同じ時計ずれで切り得る | 伝播する | 不採用: B の時計ずれを引き継ぐ。A との差は「認可と Gateway の前処理にかかった時間」だけで、いずれもプロセス内・ミリ秒級である |

A の残余: 予算 2.5 秒＋応答送信で 3 秒の期限に対し余白は 0.5 秒。前処理（多層認証はメモリ内の判定のみ）が
0.5 秒を食う経路は無い。期限を越えた場合も Gateway の `RespondAsync` の catch が拾い Bot は落ちない（従来どおり）。

## 決定

1. **項目 2（補完の時間予算）: 実装する。** `ReportCommandHandler.SuggestPeriodsAsync` が一覧照会に**2.5 秒の
   予算トークン**（呼び出し側のトークンと連結）を渡し、予算切れは**候補なしで静かに終わる**（警告ログ 1 行）。
   呼び出し側の取り消しは従来どおり伝播する。HttpClient の 5 秒は照会・確定・差し戻しと共有のため変えない。
   予算は定数 `ReportCommandHandler.SuggestionBudget` に置き、テストは内部オーバーロードで短い予算を与える
   （コンストラクタに省略可能引数を足すと組み立てガード〔W1〕の対象になるため、DI の形は変えない）。
2. **項目 3（`periodStart` の表現）: コメント是正にとどめず、挙動も直す。** 実測で「数値の 1 件が一覧ごと空にする」
   ことを確認したため（トリアージの縮小の前提が崩れた）。`periodStart` を `JsonElement?` で受け、**文字列で
   日付として解釈できるときだけ**並び替えに使い、それ以外（数値・欠落・解釈不能）は**その 1 件を末尾へ回す**
   （会話キーは残す）。会話キー（`periodKey`）は文字列のまま受ける——候補そのものであり、文字列以外で来たら
   報告書サービスとの契約違反である（従来どおり一覧ごと空＝fail-safe）。
3. **項目 1（軽い一覧のエンドポイント）: 見送り、#843 に残す。** 形の比較:

   | 案 | 形 | 利点 | 欠点 |
   | --- | --- | --- | --- |
   | a | `GET /reports/keys` → `[{periodKey, periodStart}]` | 応答の形が固定・既存 `/reports` を変えない | `/{periodKey}` と同じ階層に字面のルートが入る（`keys` を会話キーとして取れなくなる。実害は無いが規約が無い） |
   | b | `GET /reports?fields=periodKey,periodStart` | 既存ルートのまま | 射影の文法が新設で前例が無い。値の検証と応答の型が可変になる |
   | c | `GET /reports?view=keys&limit=N` | ページングも同時に入る | b と同じく前例なし。`limit` の既定値を決める根拠が要る |

   **決められる根拠が揃っていない**: どのサービスにも射影・ページングの前例が無く（実測）、通信仕様書・
   OpenAPI にも `/reports` が載っていない。報告書サービスとの配備順（新しい通知サービスが古い報告書サービスへ
   当たったときの退避）も決める必要がある。**件数は PoC 運用で数十件**であり、項目 2 の予算により一覧が遅い帯でも
   「毎打鍵で catch へ落ちる」は「候補なしで静かに終わる」へ倒れるようになった。**件数が数百件へ近づく前に**
   案 a〜c を裁定して実装する。アダプタ側は射影の形（会話キー＋開始日）を変えずに差し替えられる。
4. **項目 4（台帳の再測定手順）: 足す。** `docs/blocked-tasks.md` A-7a の再測定手順に「`/report` の `period` を
   打つと候補が出る・許可外のアカウントには候補が出ない」を追加する。#570／#565 の実機窓の前に入れる。
5. **項目 5（`GuildIdOf`）: 基底プロパティ 1 行へ畳み、コメントを実態へ直す。** 4 アームは基底
   `SocketInteraction.GuildId` を読むだけで等価（実測）。switch を残すと「型を足し忘れると DM 扱い」という
   **起きない危険**を示唆し続けるため、アームを消す。
6. **項目 6（確定済みも候補に含む）: 受容として記録する。** 空入力時にレビュー待ちより新しい報告書が 25 件以上
   あると押し出されるが、運用上レビュー待ちは最新であり、状態で絞るには IADR-0240 決定 5（状態 enum の表現に
   結合しない）を崩すか項目 1 の軽い一覧に状態を表現非依存で載せる必要がある。**再考の条件**: 項目 1 の実装時、
   または押し出しが実際に報告されたとき。記録先は IADR-0240 決定 9 の追記と `ReportPeriodSuggestions` のコメント。

## 影響範囲

| ファイル | 変更 |
| --- | --- |
| `Features/Notifications/ReviewReport/ReportCommandHandler.cs` | 補完の予算（2.5 秒）・予算切れを空へ丸める |
| `Infrastructure/ExternalServices/HttpReportReviewController.cs` | `periodStart` を `JsonElement?` で受ける・コメントの是正（項目 1/3） |
| `Infrastructure/ExternalServices/DiscordNetBotGateway.cs` | `GuildIdOf` を基底プロパティへ（項目 5） |
| `Domain/ReportPeriodSuggestions.cs` | 項目 6 の受容をコメントに記録 |
| `Tests/.../ReportCommandHandlerTests.cs` / `HttpReportReviewControllerTests.cs` | 下記の受け入れ基準 |
| `docs/blocked-tasks.md` | A-7a の再測定手順に補完の実機確認（項目 4） |
| `.ai-context/adr/IADR-0240_…md` | 決定 9 への日付つき追記 |

報告書サービス（#970 が触る `HttpOpenPositionSource` 周辺を含む）は**無改修**。

## 受け入れ基準

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | 一覧照会が予算を超えたら、補完は**例外を投げず候補なし**で終わる | 単体（ハンドラ・短い予算） |
| 2 | 一覧が予算内に返れば候補はそのまま返る（予算が正常応答を切らない） | 単体（ハンドラ） |
| 3 | 呼び出し側の取り消しは予算切れと区別され伝播する | 単体（ハンドラ） |
| 4 | 予算は Discord の期限 3 秒より短く、HttpClient の 5 秒より短い | 単体（定数） |
| 5 | `periodStart` が数値の 1 件があっても**他の候補は失われず**、その 1 件は末尾へ回る | 単体（HTTP fake） |
| 6 | 既存の補完・404 案内・認可のテストが通る | 既存単体 |
| 7 | 台帳 A-7a の再測定手順に補完の実機確認がある | 目視（diff） |

## 未検証・保留

- 実 Discord での補完の疎通・予算の効き目は未検証（A-7a の実機窓で測る。本 PR で手順を足した）。
- 項目 1 は見送り（決定 3）。#843 は `Refs` で残す。
