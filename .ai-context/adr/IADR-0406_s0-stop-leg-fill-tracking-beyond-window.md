---
title: IADR-0406 ブローカー側逆指値（S0）のレグは、保護記録が Active のあいだ約定追跡の追跡上限の対象外にし、ガードがレグの終端を観測したら完了させる前に追跡の起点をその時刻へ進める
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-05, UC-02, ADR-0040, ADR-0041, IADR-0113, IADR-0210, IADR-0344, IADR-0370, IADR-0394, IADR-0118, IADR-0159]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 損切り・FR-05 発注執行)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (S0 / S1)
---

# IADR-0406: S0 のレグを約定追跡の追跡上限から外す（保護記録が Active のあいだ）と、完了前の追跡の起点の付け直し

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: claude（[#958](https://github.com/endazon/ai-stock-trading/issues/958) の「対処の案」2 つを比べて本 IADR が決め、PR でオーナーの確認を受ける）

## 起点・関連

- 関連する計画書 ID: **FR-10**（損切り・統制）、FR-05（約定の反映）、UC-02
- 対象 Issue: [#958](https://github.com/endazon/ai-stock-trading/issues/958)（PR #949 の監査で判明）
- 関連する実装仕様書: [20260925_958_s0-fill-tracking-window](../specs/20260925_958_s0-fill-tracking-window.md)
- 関連 IADR: [IADR-0113](IADR-0113_moomoo-fill-polling.md)（約定追跡と追跡上限）、
  [IADR-0210](IADR-0210_broker-side-stop-loss-unification.md)（S0 の保護記録とガード）、
  [IADR-0344](IADR-0344_s1-software-stop-loss.md)（保護記録の機構列・S1）、
  [IADR-0394](IADR-0394_stop-out-same-day-reentry-block.md) §結果 3（この欠落の記録）、
  [IADR-0118](IADR-0118_broker-position-reconciliation.md)（建玉観測の乖離検知）、
  [IADR-0159](IADR-0159_buy-in-post-hoc-inference.md)（強制買戻しの事後推定）

## コンテキストと課題

約定追跡（`OrderFillPoller`）は記録から `FillPolling:MaxTrackingHours`（既定 24 時間）を過ぎた非終端の記録を照会しない
（IADR-0113）。S0 の決済レグの記録は**武装の時刻**で作られ、逆指値は何日も約定を待ち得る。したがって武装から 24 時間を
超えて約定した S0 の損切りは `OrderExecuted` として一度も発行されず、取引台帳へ届かない。`OrderExecuted` を出すのは
発注時と約定追跡だけであり（実測 3 箇所）、突合は出さない。IADR-0394 は S0 を**約定**で数えるので、その損切りは
数えられず、その日の同じ方向の新規建ては止まらない。台帳の建玉・実現損益にも入らない。

**建玉観測の乖離検知は拾わない**（issue の確認事項）。乖離検知（IADR-0118）は数量の食い違いを通知するだけで台帳を書かない。
利用者の取り込み（計画 ADR-0041）は数量だけで、損切りとしては数えない。🔴 ショートの S0（買い戻し）では、
強制買戻しの事後推定（IADR-0159）が「自らの決済指示で説明できない消失」として**強制買戻しと取り違え得る**
（武装の承認は処理中の窓 30 分をとうに過ぎている）。

## 検討した選択肢

issue の「対処の案」2 つと、上限の延長（比較のため）。規則 11 に従い、増える側（24 時間を超えた S0 の約定が届くか）と
減る側（照会件数が増えないか）の両方で比べた（実測は仕様書の表と T-10-866）。

| 案 | 内容 | 増える側 | 減る側 | 採否 |
| --- | --- | --- | --- | --- |
| 0 | 追跡上限を延ばす（例 7 日） | 7 日までは届く | 🔴 上限内の全ての非終端記録が 7 日照会される | 不採用 |
| 1 | 保護記録が Active のあいだ、S0 のレグを上限の対象外にする（issue の案 1） | 🔴 ガードが先に `Filled` を見て保護記録を完了させると、次の約定追跡では対象外から外れて**落ちる**（ガードと約定追跡は別々の 30 秒の巡回で順序が決まらない。本修正の前でも半々で起きる並び） | 増えない（Active な S0 のレグだけ＝保有建玉の数） | 単独では不採用 |
| 2 | 追跡の起点を「最後に有効を確かめた時刻」にする（issue の案 2） | ガードが毎巡回（30 秒）確かめるなら届く | 増えない | 単独では不採用（下記） |
| **1＋2′** | 案 1 に加え、ガードが**レグの終端を観測したとき（約定・失効）と取り消したときだけ**、保護記録を完了させる前にそのレグの記録の起点を観測の時刻へ進める | 届く（順序に依らない） | 増えない（Active な S0 のレグだけ） | **採用** |

案 2 を単独で採らない理由:

- **書き込みが巡回ごとに増える。** 「最後に有効を確かめた時刻」を持つには、ガードが Active な S0 のレグごとに 30 秒おきに
  記録を書く。案 1＋2′ はレグの寿命に 1 回だけ書く。
- **ガードが止まると窓が武装の時刻へ戻る。** ガード（`ProtectiveStopGuard:Enabled`）を止めた構成では確かめる者がいない。
  案 1 はガードに依らず、保護記録が Active である限り約定追跡が追う（ガードが止まれば保護記録は完了しない）。
- **起点を別の列に持つならスキーマが変わる。** `ExecutedAt` を使わず新しい列を足すとマイグレーションが要り、
  並行 PR（#950 / #956）が同じ DbContext のモデルスナップショットへマイグレーションを足しているところへ衝突を持ち込む。
  2′ は既存の `ExecutedAt` を**非終端のあいだだけ**動かすので、スキーマは変わらない（下の決定 3 の注記）。

もう 1 つ、案 1 の欠けを「ガードが `Filled` を見ても、レグの記録が終端になるまで保護記録を完了させない（待つ）」で
埋める形も試した（下の「却下した形」）。

## 決定

### 決定 1: 発注結果ストアに 2 つの口を足す（EF・インメモリ）

- `FindPendingByOrderIds(orderIds)`: 指定した注文 ID のうち非終端の記録を古い順に返す。**追跡上限は見ない**
  （対象を絞るのは呼び出し側）。空集合なら何も読まない。
- `RenewTracking(orderId, trackedFrom)`: 非終端の記録の `ExecutedAt`（追跡の起点）を `trackedFrom` へ**進める**。
  **時刻の列だけを書く**——EF は変更追跡により `executed_at` だけを UPDATE し、ガードが非終端として読んだ後に
  約定追跡が別のスコープで終端を書いても、状態・数量を古い値で巻き戻さない。記録が無い・終端・起点が既に同じか新しい
  なら何もしない。

### 決定 2: 約定追跡は、Active な S0 の保護記録の現試行の逆指値レグを追跡上限の対象外にする

`OrderFillPoller` は省略可能な `IProtectiveStopOrderStore` を受け取り、巡回のたびに `FindActive(batchSize)` のうち
S0（`!IsSoftwareStop`）かつ `StopOrderId` が空でない行の注文 ID を集め、`FindPendingByOrderIds` で得た記録を
追跡上限内の記録へ（注文 ID で重複を除いて）足す。以降（照会・更新・発行・S1 の再武装）は既存と同一。
**足すのは Active な S0 のレグだけ**なので、照会件数の増分は保有建玉の数で頭打ちになる。
足す側の読み取り（保護記録・注文 ID 指定の抽出）が失敗しても、その巡回は追跡上限内の記録だけを追跡し、Error ログを残す
（足す側の失敗で通常の追跡まで止めない。S0 のレグは Active のあいだ次の巡回で再び足される）。
保護記録ストアを渡さない構成は従来どおり。`Program.cs`（moomoo 選択時）は渡す。

### 決定 3: ガードは S0 のレグの終端を観測したら（または取り消したら）、保護記録を完了させる前に追跡の起点を進める

`ProtectiveStopGuard.EvaluateAsync`（S0 の評価）の 3 箇所で、`MarkCompleted`・再発注より**前に**
`store.RenewTracking(stop.StopOrderId, now)` を呼ぶ。

- 逆指値の `Filled`（損切りの成立）
- 建玉消滅で逆指値を取り消した直後（取消の直前までの部分約定を拾わせる）
- 失効（`Cancelled` / `Rejected` / `Expired`）。完了・再発注のどちらでも、保護記録の現試行がこのレグから離れる前に行う

付け直された記録は窓の内側へ戻るので、ガードと約定追跡のどちらが先に巡回しても、約定追跡は次の巡回で終端を観測し
`OrderExecuted` を発行する（T-10-866 が両方の順序で固定）。付け直しの例外は上へ上げる——保護記録を完了させずに
次の巡回でやり直す側へ倒す（完了させてから失敗すると、そのレグは二度と照会されない）。

🔴 **`ExecutedAt` を動かしてよい根拠**: 非終端の記録の `ExecutedAt` を読むのは約定追跡の窓と、非終端の進捗で発行する
`OrderExecuted` の時刻だけである（`grep -rn "ExecutedAt" backend/Services/OrderExecutionService --include=*.cs` の実測）。
付け直しはレグの終端を観測した後にしか行わないので、次に約定追跡が見るのは終端であり、発行の時刻は終端の時刻
（`snapshot.CompletedAt ?? now`）で上書きされる。約定追跡が既に終端を書いていれば付け直しは何もしない。

### 却下した形: ガードが `Filled` を見ても、レグの記録が終端になるまで保護記録を完了させない

約定追跡の反映を待つあいだ、**約定済みの S0 の行が Active のまま残る**。S0 と S1 が同じ銘柄・方向に併存する群では、
巡回の先頭の外部要因の観測（`ProtectiveStopNetting.ReconcileShares`・IADR-0344 追記(7)）が、S0 の約定で減った建玉を
「外部要因の減少」として**S1 の行から先に**割り当てる。待ちがガードの 2 巡回に跨がると観測が確定し、
**S1 の残保護数量が黙って削られる**（生きている建玉の保護が消える）。決定 3 は保護記録を従来どおりその巡回で完了させるので、
この副作用を持ち込まない。

### 変えないもの

- 追跡上限の既定値・クランプ（`FillPollingOptions`）、`FindPendingSince` の意味
- 保護記録ストア（`IProtectiveStopOrderStore` とその 2 実装）・マイグレーション・スキーマ（`FindActive` を読むだけ）
- S1 の決済レグ（IADR-0389 の「追跡上限を過ぎた決済レグは照会対象から外れる」）・エントリー・通常の成行
- 取引台帳の側（`OrderExecutedLedgerHandler` は同じ注文の累積数量の単調 upsert で冪等。二重の発行は無変更になる）

## 理由

- 欠落は「S0 のレグの記録の時刻が武装の時刻」であることと「約定を観測した者が完了を先に書く」ことの 2 点から生じる。
  決定 2 が前者（Active のあいだ）、決定 3 が後者（完了の瞬間）を埋め、どちらも S0 のレグに限る。
- 決定 3 の書き込みはレグの寿命に高々数回（終端の観測は 1 回、失効後の再発注不可が続く場合は巡回ごと。後者も約定追跡が
  次の巡回で記録を終端にすると止まる）。

## 結果・残余リスク

- 武装から 24 時間を超えて約定した S0 の損切りは、約定の時刻で `OrderExecuted` として発行され、台帳へ届き、
  IADR-0394 が数える（T-10-862・T-10-865・T-10-866）。IADR-0394 §結果 3 の欠落は解消する。
- **照会件数**: 追跡上限を過ぎた記録で照会が増えるのは Active な S0 のレグだけ（T-10-863）。
- **残余 1（乖離の取り込みで完了した S0）**: 利用者が乖離を取り込んで S0 の保護記録が完了した（`ProtectiveStopDriftAdopter`・
  [IADR-0370](IADR-0370_drift-adoption-protective-stop-followup.md)）後に、残った逆指値が 24 時間を超えて約定した場合は拾えない。取り込みはシステム外の売買の後であり、
  ガードは建玉消滅で残存逆指値を取り消す（決定 3 の取消の分岐で付け直す）ので、実際に残るのは取り込みから取消までの間。
- **残余 2（照会が不明のまま）**: ガードが終端を観測した後、約定追跡の照会が 24 時間ずっと不明（`null`）なら再び窓から外れる。
  約定追跡の既存の fail-safe（不明は据え置き）のままである。
- **残余 3（S1 の行の機構）**: S1 の行は `StopOrderId` が空であり、`!IsSoftwareStop` の絞り込みは空の注文 ID の除外と
  重なる（変異「S1 を除外しない」は赤にならない等価変異。意図を明示するために残す）。
- **残余 4（台帳に届くまでの遅れ）**: 約定から台帳までの遅れは従来どおり約定追跡の巡回間隔（既定 30 秒）である。
- 試験: T-10-860〜T-10-866（テスト仕様書 FR-10 に記す）。変異注入の実測も同書。

## 関連

- [#958](https://github.com/endazon/ai-stock-trading/issues/958)
- 実装: `Features/OrderExecution/PollOrderFills/OrderFillPoller.cs`・`Features/OrderExecution/GuardProtectiveStops/ProtectiveStopGuard.cs`・
  `Features/OrderExecution/IExecutedOrderStore.cs`・`Infrastructure/Persistence/{Ef,InMemory}ExecutedOrderStore.cs`・`Program.cs`
- テスト: `BrokerStopLegFillTrackingTests`・`ExecutedOrderStoreStopLegTrackingTests`・`OrderFillPollingServiceTests`
