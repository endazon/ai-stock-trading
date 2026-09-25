---
title: IADR-0405 S3 の試行の記録にはブローカーが採番した注文 ID だけを載せ、監査台帳の自由記述欄へ入る理由文は上限 500 文字・接続先を伏せてから記録する
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-11, FR-12, ADR-0040, IADR-0347, IADR-0211, IADR-0227, IADR-0019]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1 の S3)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-11 監査・NFR-10 保持)
---

# IADR-0405: S3 の試行の記録から捏造 ID と基盤の接続先を除き、理由文に上限を置く

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: claude（起票 #842。利用者レビューは PR で受ける）

## 起点・関連

- 対象 Issue: #842（#838 ＝ S3 の監査で残った非ブロッキング 5 件）のうち**論点 1・2**。論点 4 は仕様書の追記だけで
  判断を伴わない。論点 3・5 は S3 の受理の実測待ち（#809 の裁定依存）で本 IADR の射程外。
- 関連する実装仕様書: [20260925_842_s3-audit-followups](../specs/20260925_842_s3-audit-followups.md)
- 関連 IADR: [IADR-0347](IADR-0347_alternative-broker-order-types-for-simulate.md)（S3。決定 1 の戻り値と決定 6 の
  試行の記録を**補う**。覆さない）、[IADR-0227](IADR-0227_audit-payload-secret-exposure-guard.md)（秘匿名の全数走査
  `AuditPayloadSecretExposureTests`。欄の**名前**を見る側。本 IADR は欄の**中身**を見る側）、
  [IADR-0211](IADR-0211_opend-unavailable-forgo-without-queueing.md)（接続確立の失敗は丸めず伝播）

## コンテキストと課題

1. **拒否された S3 の保護レグに、実在しない注文 ID が載る。** `MoomooBrokerAdapter.Terminal()` は送信前棄却と
   確認済み拒否（`retType=-1`）で `Guid.NewGuid()` を注文 ID として合成する（`BrokerOrder.OrderId` が非 null の契約で
   あるため）。発注執行はそれを `AlternativeProtectiveStopAttempted.BrokerOrderId` へそのまま載せ、7 年保持の監査台帳に
   「moomoo へ問い合わせても存在しない注文 ID」が残る。契約コメントは「発注できたときのブローカー注文 ID」と書いている。
2. **監査台帳の自由記述欄へ任意長の `Exception.Message` が入る。** S3 の発注が例外で落ちると
   `RejectReasonMessage = ex.Message` になる。接続確立の失敗では OpenD のホスト:ポートが入る（実測
   `OpenD への InitConnect が失敗しました（opend.ai-stock-trading.svc:11111）。`）。台帳の `Summary` は 200 字で切られるが、
   `Detail`（イベント全量 JSON）は**上限なし・伏せ字なし**である。契約イベントへ `ex.Message` を載せる経路は
   本リポジトリでこれ 1 本である（作業仕様書の母集合）。

## 検討した選択肢

### 論点 1

1. **`Terminal()` の注文 ID を空文字にする** — `BrokerOrder.OrderId` を使う全経路（エントリーの `ExecutionRecord`・
   `OrderExecuted`・予約の確定・取消の照会）の意味が変わる。空 ID で取消・照会を撃つ経路の安全性を全部見直す必要があり、
   論点 1 の射程を超える。**却下**。
2. **`BrokerOrder.OrderId` を nullable にする** — paper を含む全ブローカー・全呼び出し側の契約改訂。同上。**却下**。
3. **発注執行で「状態が Rejected なら null」にする** — ブローカーが ID を採番したうえで拒否状態（`SubmitFailed` 等）を
   返した場合、**実在する ID まで捨てる**。「採番されたか」は状態からは決まらない。**却下**。
4. **能力ポートの戻り値 `AlternativeProtectiveOrderPlacement` に `BrokerOrderId` を足し、アダプタが決める**（採用）。
   合成か実在かを知っているのはアダプタだけであり、S3 の戻り値は元々「拒否理由を持ち帰る」ための専用型である
   （IADR-0347 決定 1）。S0・エントリー・paper の契約は 1 行も変わらない。

### 論点 2

1. **発行側（発注執行）で `ex.Message` を整える** — 台帳へ入る経路が将来増えるたびに各発行側が同じ規律を持つ必要がある。
   また発行側のログ（7 年保持されない）には全文を残したい。**却下**（台帳の境界で一度だけ整える）。
2. **例外の種類だけを記録し文面を捨てる** — 送信後に結果を確認できない失敗の文面は内側の `retType` / `retMsg` を含み、
   S3 の目的（なぜ使えないかを台帳から読む）に要る。**却下**。
3. **監査の写像（`AuditEntryFactory`）で上限と伏せ字を掛けた写しから要約と全量 JSON の両方を作る**（採用）。

## 決定

1. **`AlternativeProtectiveOrderPlacement.BrokerOrderId`（`string?`）を足す。** アダプタはブローカーの応答が返した
   注文 ID（`MoomooOrderResult.OrderId`）だけを入れ、送信前棄却と確認済み拒否（`Terminal` の合成）では **null** にする。
   ブローカーが ID を返したうえで拒否状態だった場合は、その実在する ID を残す。発注執行は
   `AlternativeProtectiveStopAttempted.BrokerOrderId` へ `placement.BrokerOrderId` を載せる。`Order.OrderId`
   （合成値を含む）は従来どおり `BrokerOrder` の契約を満たすためだけに残る。
2. **監査台帳の自由記述欄の線引きと上限**（`AuditService.Domain.AuditFreeText`）:
   - **載せてよい**: ブローカーの応答文（`retMsg`）・発注前検証の定型文・例外の種類と要旨。
   - **伏せる**: 接続先 —— URL（`scheme://…`）・`[IPv6]:port`・IPv4（`:port` の有無を問わない）・英字を含むドット区切りの
     `ホスト名:port`・`localhost:port` を `［接続先］` に置き換える。時刻（`10:30`）・価格（`329.03`）・`retType=-1`・
     `[1]` は拾わない（テストで固定）。境界は ASCII の文字だけで判定する（`\w` は日本語を含み「接続先127.0.0.1」を取り逃がす）。
   - **畳む**: 改行・タブ等の制御文字は空白 1 つ。
   - **上限**: **500 文字**。超過は切り詰めて `…` を付ける（サロゲートペアを割らない）。実測の最長級
     （送信後に結果を確認できない旨＋内側の `retMsg`）で約 250 文字であり、2 倍の余裕を取った。
   - 正規表現は語の先頭からしか始まらず語の中を原子グループで読む（任意長の入力でバックトラックが 2 乗化し、
     タイムアウトで理由文ごと失われることを 1 万文字の入力で実測したため）。それでも 200 ms で打ち切られた場合は
     **文面を載せず**「整形できなかった」旨だけを残す（伏せ字を保証できない文面を台帳へ入れない）。
   - **［2026-09-25 追記 / #984］** 直前の項（正規表現の形と 200 ms での打ち切り・定型文への置き換え）は
     [IADR-0419](IADR-0419_audit-free-text-linear-time-redaction.md) が置き換えた。マッチタイムアウトは**壁時計**で測るため、
     CPU が混むと数 ms の仕事でも打ち切られ、**本番でも理由文が台帳から黙って欠けた**（試験は負荷下で 12 回中 2 回赤）。
     照合は `RegexOptions.NonBacktracking`（入力長に線形）で予算を持たず、定型文への置き換えは削除した。線引き（伏せる形・
     伏せない形）・畳み・上限 500 文字は変えない（旧パターンとの差分試験で固定）。作業仕様書 `20260925_984_audit-free-text-linear-redaction`。
3. **適用するのは `AlternativeProtectiveStopAttempted.RejectReasonMessage`**（本リポジトリで唯一の、例外メッセージが
   契約イベント経由で台帳へ入る経路）。`AuditEntryFactory.From(AlternativeProtectiveStopAttempted)` が整えた写しから
   `Summary` と `Detail` の両方を作る。発行側のイベントとログは従来どおり全文を運ぶ（ログは 7 年保持されない）。

## 理由

- 捏造 ID の判定は**アダプタの中でしか正しく決まらない**（状態からは決まらない）。S3 専用の戻り値に持たせれば、
  他経路の契約を動かさずに論点 1 を閉じられる。
- 台帳は 7 年消せない（NFR-10）。**境界で一度だけ**整えれば、発行側が増えても規律が 1 か所に留まる。
  要約だけ切っても全量 JSON に全文が残るので、両方を同じ写しから作る。

## 結果・残余リスク

- 良い影響: S3 の試行の記録から合成 ID が消え、接続先が台帳に残らなくなる。理由文の長さに上限が付く。
- 🔴 **エントリー／S0 の拒否でも `Terminal` の合成 ID は `BrokerOrder.OrderId` に載る。** エントリーの拒否は
  `ExecutionRecord` と `OrderExecuted.BrokerOrderId`（監査台帳）へ入る。#842 の論点 1 と同型であり、`BrokerOrder` の
  契約改訂を伴うため本 IADR の射程外とした（別 issue で扱う候補）。
- 伏せ字は形で判定するため、**ドットを含まない単一ラベルのホスト名＋ポート**（例 `opend:11111`）は伏せない
  （`Error:404` 等の誤検出を避けるため）。現行の構成値（`opend.ai-stock-trading.svc`・`127.0.0.1`）は伏せる形である。
- 他の自由記述欄（`TradeDecisionMade.Rationale` 等。LLM の出力）は本 IADR の対象外（要約の 200 字切りのまま）。
