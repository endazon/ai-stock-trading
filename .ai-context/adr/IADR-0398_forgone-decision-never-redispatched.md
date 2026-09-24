---
title: IADR-0398 見送った承認（DecisionId）は発注執行の予約表に終端（Forgone）として残し、同じ承認の再配送では発注しない — 見送りを主張してよいのは記録できたときだけ
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-10, FR-11, UC-06, ADR-0002, ADR-0024, IADR-0057, IADR-0059, IADR-0074, IADR-0117, IADR-0211, IADR-0355, IADR-0356, IADR-0362]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05 発注執行 / FR-10 手仕舞いと損切りは止めない)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-06 利用者による建玉の手仕舞い)
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md (OpenD 常駐・SPOF・再起動中は発注不可)
---

# IADR-0398: 見送った承認は予約表に終端として残し、同じ承認の再配送では発注しない

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: claude（起票 [#876](https://github.com/endazon/ai-stock-trading/issues/876)。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: FR-05（発注執行）、FR-10（手仕舞いと損切りは止めない）、FR-11、UC-06、
  ADR-0002 / ADR-0024（OpenD 常駐・SPOF。再起動中は発注不可）
- 対象 Issue: [#876](https://github.com/endazon/ai-stock-trading/issues/876)（[IADR-0356](IADR-0356_forgone-dispatch-releases-in-flight-close.md) の残余リスク 3）
- 関連する実装仕様書: [20260925_876_forgone-decision-never-redispatched](../specs/20260925_876_forgone-decision-never-redispatched.md)
- 関連 IADR: [IADR-0211](IADR-0211_opend-unavailable-forgo-without-queueing.md)（見送りの定義。**決定 3(a) の「予約を解放」を本 IADR が改める**）、
  [IADR-0356](IADR-0356_forgone-dispatch-releases-in-flight-close.md)（見送りで台帳の在庫を戻す。本 IADR はその前提を発注執行側で成立させる）、
  [IADR-0057](IADR-0057_order-dispatch-idempotency.md)（予約の 3 相。**変えない**）、
  [IADR-0059](IADR-0059_dedupe-retention-purge.md)（保持期間パージ。**Forgone は対象外**）、
  [IADR-0074](IADR-0074_reservation-reconciliation.md) / [IADR-0362](IADR-0362_reservation-reconciliation-enabled-with-release-gate.md)（突合と解放の門。**変えない**）、
  [IADR-0117](IADR-0117_owner-position-close-path.md)（台帳の終端の単調性。**変えない**）、
  [IADR-0355](IADR-0355_close-order-broker-position-gate.md)（決済前の建玉照会。予約前の見送り 2 理由の出所）

## コンテキストと課題

IADR-0356 は、見送り（`OrderDispatchForgone`）のうち「確実に未発注」の理由を受けたら、取引台帳の承認を終端
（`MarkForgone`）にして「処理中の決済」から外すと決めた。台帳の終端は単調で戻らない（IADR-0117 改定 1）。
**この判断は「その DecisionId はもう送られない」を前提にしている**が、発注執行はそれを保証していなかった。

- 接続確立の失敗（`BrokerUnavailable`）は予約（相 2）を取った**後**に起きる。発注執行は `Release`＝予約行の**削除**を
  していた（IADR-0211 決定 3(a)）。
- それ以外の 6 理由は予約を取る**前**に `return` する。予約行は**そもそも作られない**。
- 相 1 の完了判定（`FindByDecisionId`）は `ExecutionRecord` しか見ず、見送りは記録を残さない（IADR-0211 決定 3(b)）。

したがって同じ `OrderApproved` が重複配送されると（at-least-once・ack 喪失）、相 1 を素通りし、`TryReserve` が成功し、
**そのとき OpenD が復帰していれば本物の注文が出る**。決済なら台帳の押さえの外で生きる決済になり、利用者の 2 本目の手仕舞いが通る
（二重決済でショート化）。

**現行 develop（`d0165177`）で再検証した**（推測ではない）: 本 PR のテストを是正前のコードに当てると、
接続確立の失敗（Close / Open）と予約前の見送り（建玉照会の不明／建玉なし）の 4 ケースがすべて赤
（`Expected broker.PlaceCount to be 0, but found 1`。Open はエントリーと保護レグで `found 2`）。
🔴 **issue 本文は接続確立の失敗だけを挙げていたが、予約前の見送りにも同じ穴があった**（台帳の allowlist は
`BrokerPositionsIndeterminate` / `BrokerPositionAbsent` も `true`＝在庫を戻す。照会が回復した後の再配送で送れる）。

## 検討した選択肢（issue #876 が列挙した 4 案）

1. **`MarkForgone` を戻す経路を持つ**（同じ DecisionId の `OrderExecuted` で台帳の終端を解除する）— 台帳の単調性
   （#848 以来の不変条件）に例外を作る。しかも `OrderExecuted` が台帳に届くまでの窓（発注 → 発行 → 購読）は押さえが外れたまま
   であり、その間の 2 本目の手仕舞いは止まらない。**却下**。
2. **再配送を「見送り済みの DecisionId」で弾く**（発注執行に見送りの記録を残す）— 判定位置としては正しい。記録先を
   新設すると（表・保持期間・突合との関係）が増える。**判定位置だけ採る**（下の 3 と組み合わせる）。
3. **予約を解放せず別の状態で残す**（採用）— 予約表は既に「DecisionId ごとに高々 1 行」「発注前にコミット」「突合・パージの述語が状態で絞る」を
   持っている。状態を 1 つ足すだけで記録先・一意性・可観測性がそろう。
4. **何もしない（露出を受容する）** — 前提条件（重複配送＋直後の OpenD 復帰）は稀だが、帰結が二重決済（不可逆・損失が限定されない）であり、
   しかも予約前の見送りでは OpenD の復帰すら要らない（照会の回復だけで足りる）。**却下**。

## 決定

### 決定 1: 予約表の状態に `Forgone`（見送り＝確実に未発注の終端）を足す

`OrderDispatchState.Forgone = 2`。**末尾へ追加する**。`State` 列は整数で制約も変換も無いため **Migration は不要**
（モデルスナップショットは `b.Property<int>("State")` のまま変わらない）。

- `CompletedAt` に見送りを記録した時刻を入れる。予約を取る前の見送りは `ReservedAt` も同じ時刻（行を作った時刻）。
  `BrokerOrderId` は `null`（注文は存在しない。捏造しない）。
- `FindStalledReserved`（突合）・`PurgeCompletedBefore`（保持期間パージ）・`Release`（突合の解放）の述語は
  既に `Reserved` / `Completed` で絞っており、**`Forgone` はどれにも載らない**（コードを変えずに成立。T-10-828 で固定）。
- `TryReserve` は行が在れば `false` なので、見送った DecisionId では予約を取り直せない。

### 決定 2: 見送りを主張してよいのは、`Forgone` を記録できたときだけ

予約表に 2 つの口を足す（戻り値は `ForgoneRecordOutcome`。**4 つを取り違えない**）:

| 口 | 無し | `Forgone` | `Reserved` | `Completed`・未定義 |
| --- | --- | --- | --- | --- |
| `TryRecordForgone`（予約前の見送り） | 挿入 → `Recorded` | `AlreadyForgone` | **`HeldByReservation`（行を変えない）** | **`AlreadyCompleted`（行を変えない）** |
| `MarkReservationForgone`（自分の予約の見送り） | 挿入 → `Recorded` | `AlreadyForgone` | 移す → `Recorded` | **`AlreadyCompleted`（行を変えない）** |

発注執行は `Recorded` / `AlreadyForgone` のときだけ `OrderDispatchForgone` を返す。

- 🔴 **`HeldByReservation`**: 同じ承認の別の配送が予約を持っている（発注に着手済み＝**送ったか不明**）。ここで見送りを発行すると、
  台帳が生きているかもしれない決済の押さえを解く——**「確実に未発注」と「送ったか不明」を混ぜない**（Principle A）。
  従来の予約競合と同じ `OrderDispatchReservationConflictException` で止める（再試行のうちに相手が確定すれば相 1 が既存結果を再発行する）。
  🔴 是正前はこの競合でも見送りを発行していた（予約前の見送りは予約表を見なかった）。本決定はその穴も閉じる。
- 🔴 **`AlreadyCompleted` と未定義値**: 発注済みを見送りと書き換えない。`InvalidOperationException`（見送りを主張しない側へ倒す）。
  ストアも**未定義の状態値は `AlreadyCompleted` 側へ倒す**（将来の状態を知らない版が読んでも見送りを主張しない）。
- 記録は `SaveChanges` で**コミットしてから戻る**（`TryReserve` と同じ規律）。発行は結果が呼び出し側へ戻った後なので、
  **記録は常に発行より先**である。記録できなければ例外で止まり、見送りは発行されない（台帳は押さえたまま＝安全側）。
- 一意キー競合の判定は `TryReserve` と同じく「行が実在するか」で行う（#714 / IADR-0317 / IADR-0319）。

### 決定 3: 相 1 の直後で `Forgone` を見たら、ブローカーに一切触れずに戻る

`ExecuteAsync` は相 1（`FindByDecisionId`）の直後に予約表を読み、`Forgone` なら**発注も建玉照会もせず**
`OrderDispatchResult.ForgoneReplaySuppressed` を返す。ハンドラは**何も発行しない**（`OrderExecuted` も `OrderDispatchForgone` も）。

- 見送りイベントを再発行しないのは、**理由を記録していない**からである（理由を捏造しない。列を足せば Migration が要り、
  並行する PR が同じスナップショットを触っている）。ログは Warning で「発注しない・見送りは再発行しない・注文は送られていない」を残す。
- ハンドラは従来「見送りでなければ発注結果がある」と読んでいた（`result.Executed!`）。3 つ目の形を**先に**返さないと
  NullReferenceException で共通再試行へ落ちる（T-10-830）。

### 決定 4: 接続確立の失敗は `Release`（削除）をやめ、`MarkReservationForgone` にする

IADR-0211 決定 3(a)「予約を解放（確実に未発注のため二重発注の窓は無い）」を改める。**その時点で**二重発注の窓が無いのは正しいが、
解放は**同じ承認の再配送に発注を許す**——これは同決定 3 の「再発注は次の取引判断からのみ（見送った注文の自動リプレイ経路を作らない）」
が禁じたリプレイそのものである。**改めるのは (a) の手段だけで、決定 3 の趣旨はむしろ強まる。**

### 変えないもの

- 見送りの理由・イベントの形・通知・監査・メトリクス。リスク管理のコード（allowlist・`MarkForgone`・窓 30 分）は 1 バイトも変えない
  （コメントの是正のみ）。
- 予約の 3 相（IADR-0057）、送信後の不明（`BrokerDispatchIndeterminateException`）は `Reserved` のまま据え置く（IADR-0117 改定 6）。
- 突合の `Release`（`NotPlaced`）と門 `Reconciliation__ReleaseOnNotPlaced=false`（IADR-0362）。見送りを発行しない経路であり射程外。
- 保護逆指値ガード・S1 実行器・エントリー直後の建玉解消の `Release`。いずれも自前の決済 DecisionId で**次の巡回で撃ち直すのが設計**で、
  `OrderDispatchForgone` を発行しない（台帳の見送りによる在庫解放と無関係）。

## 理由

- 台帳が在庫を戻す根拠（「確実に未発注」）を、**時点の事実**から**DecisionId の恒久的な事実**へ引き上げる必要があった。
  それを保証できるのは発注執行だけであり、発注前にコミットする予約表が最も近い置き場である。
- 3 つの不変条件（台帳の単調性・予約の 3 相・見送りの定義）のどれも崩さない。変わるのは「見送った予約行を消すか残すか」だけである。
- 「手仕舞いを止めない」（FR-10）は崩れない。見送られた決済は注文として存在せず、利用者の再要求は**新しい DecisionId**
  （`PositionCloseService` は要求ごとに `Guid.NewGuid()`）で通る。維持率割れの自動縮小も毎回新しい DecisionId である。
  同じ DecisionId の発注を取引判断が意図して繰り返す経路は無い（`new OrderApproved(` の 3 箇所を確認）。

## 結果・残余リスク

- 見送った承認の再配送は、OpenD や建玉照会が回復していても発注されない（T-10-823・T-10-824・T-10-831）。
- 🔴 **残余リスク 1（発行の喪失）**: `Forgone` を記録した後、見送りイベントが発行されるより前にプロセスが落ちると、
  再配送は抑止されて**見送りイベントは二度と出ない**（理由を記録していないため）。このとき台帳は承認を窓（30 分）のあいだ
  「処理中の決済」として押さえ（#852 以前の挙動＝安全側。二重決済は起きない）、見送りの通知は届かず、抑止時の Warning ログだけが残る。
  是正前の同じ窓は「再配送で撃ち直し得る」だった（こちらは二重決済の側）。durable outbox が配線されていない（IADR-0362 の残余リスク）ことと同根である。
- **残余リスク 2（行の累積）**: `Forgone` 行はパージしない。消すと保持期間の後の再配送で穴が戻るからである（再配送が
  何日後に来ないとは言い切れない）。行数は見送りの件数（OpenD の停止中・fail-closed の見送り）で増える。
  パージを足すなら、相 1 の記録（`ExecutionRecord`）に相当する「もう送らない」の恒久記録を別に持ってからにすること。
- **残余リスク 3（DB が落ちている見送り）**: 予約前の見送りも記録のために DB に書くようになった。書けなければ例外で止まり、
  見送りは発行されない（共通再試行 → error キュー。台帳は窓のあいだ押さえる＝安全側）。是正前はこの場合も見送りを発行していた。
  注文は送らない点は変わらない。
- IADR-0356 の残余リスク 3 は本 IADR で解消した（同 IADR に日付つき追記）。
