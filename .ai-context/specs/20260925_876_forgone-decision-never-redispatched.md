---
title: 見送った承認（DecisionId）を発注執行の予約表に終端として残し、同じ承認の再配送では発注しない
type: spec
status: accepted
related_ids: [FR-05, FR-10, FR-11, UC-06, ADR-0002, ADR-0024, IADR-0057, IADR-0059, IADR-0074, IADR-0117, IADR-0211, IADR-0355, IADR-0356, IADR-0362, IADR-0398]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05 発注執行 / FR-10「手仕舞いと損切りは止めない」)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-06 利用者による建玉の手仕舞い)
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md (OpenD 常駐・SPOF・再起動中は発注不可)
---

# 仕様書: 見送った承認は再配送されても発注しない（#876）

## 起点

- #876（IADR-0356 の残余リスク 3。PR #872 のフェーズ末監査が指摘）。
- 症状（issue 本文の再現順序）: 決済の承認が接続確立の失敗（`BrokerUnavailableException`＝確実に未発注）で
  見送られる → 発注執行は予約行を**削除**して `OrderDispatchForgone` を発行 → リスク管理は台帳の承認を
  `MarkForgone` で終端にし「処理中の決済」から外す（在庫が戻る。#852 の目的）→ **同じ `OrderApproved` が
  重複配送される** → 相 1 の `FindByDecisionId` は null・`TryReserve` は成功 → OpenD が復帰していれば
  **本物の決済注文が出る** → 台帳の `TerminalAt` は単調で戻らず、その決済は処理中に数えられない →
  利用者の 2 本目の手仕舞いが通る（二重決済でショート化）。

## 再検証（着手前・現行 develop `d0165177`）

#914（突合の per-item 発行）ほか今週の予約・突合の PR がこの穴を塞いでいないかを、**コードとテストの両方**で確かめた。

- コード: `OrderExecutionAppService.ExecuteAsync` の `catch (BrokerUnavailableException)` は
  `reservations.Release(approved.DecisionId)` のまま。`EfOrderReservationStore.Release` は `Remove(row)`。
  相 1 は `store.FindByDecisionId` だけを見る。**塞がれていない。**
- テスト（本 PR の `OrderExecutionServiceForgoneReplayTests` を是正前のコードに当てた実測）:
  4 ケースすべて赤（`Expected broker.PlaceCount to be 0, but found 1`／Open は `found 2`＝エントリーと保護レグ）。
- 🔴 **issue 本文の射程より広い**（本仕様で新たに見つけた）: 予約を取る**前**に見送る理由
  （`BrokerPositionsIndeterminate` / `BrokerPositionAbsent` ほか）は予約行を**そもそも作らない**。
  台帳の allowlist はこの 2 つも `true`（在庫を戻す）なので、**照会が回復した後の重複配送**で同じ穴が開く
  （上の実測の `indeterminate: True/False` の 2 ケース）。

## 裁定（issue が列挙した 4 案から選ぶ。詳細と却下理由は IADR-0398）

**案 3（予約を解放せず別の状態で残す）を採り、案 2（見送り済みの DecisionId で再配送を弾く）の判定位置を併用する。**

- 予約表 `order_dispatch_reservations` の状態に **`Forgone`（見送り＝確実に未発注の終端）** を足す。
  **見送りを発行する前に必ず記録する**（記録できなければ見送りを主張しない）。
- `ExecuteAsync` は相 1 の直後で予約表を読み、`Forgone` なら**ブローカーに一切触れずに**戻る
  （発注も建玉照会もしない。イベントも発行しない）。
- 根拠: IADR-0211 決定 3 は「**再発注は次の取引判断からのみ**（見送った注文の自動リプレイ経路を作らない）」と
  既に決めている。同じ DecisionId の再配送で発注されるのは、この決定が禁じた自動リプレイそのものであり、
  予約の解放（決定 3(a)）がそれを可能にしていた。**覆すのは (a) の「解放」だけで、決定 3 の趣旨は強まる。**
- 台帳の単調性（IADR-0117 改定 1）・予約の 3 相（IADR-0057）・見送りの定義（IADR-0211）はいずれも**変えない**。
  リスク管理側のコードは 1 バイトも変えない（コメントの是正のみ）。

## 射程

1. `OrderDispatchState.Forgone = 2` を足す（**Migration 不要**: `State` は整数列で、制約も変換も無い。
   モデルスナップショットは `b.Property<int>("State")` のままで変わらない）。
2. `IOrderReservationStore` に 2 つの口を足す（EF・InMemory の両実装とテストの偽物 2 つ）。
   - `TryRecordForgone(decisionId, forgoneAt)` — 予約を取る**前**の見送り。行が無ければ `Forgone` で挿入する。
   - `MarkReservationForgone(decisionId, forgoneAt)` — 自分が取った `Reserved` を `Forgone` へ移す（接続確立の失敗）。
   - 戻り値は列挙 `ForgoneRecordOutcome`（`Recorded` / `AlreadyForgone` / `HeldByReservation` / `AlreadyCompleted`）。
     🔴 **4 つを取り違えない**: `Reserved` の行（別の配送が発注に着手済み＝送ったか不明）と `Completed` の行
     （発注済み）がある DecisionId について**見送りを主張してはならない**（主張すると台帳が生きている注文の押さえを解く）。
3. `ExecuteAsync`:
   - 相 1 の直後に「見送り済みなら発注しない」判定を置く（結果は新しい形 `OrderDispatchResult.ForgoneReplaySuppressed`）。
   - 予約前の見送り 8 箇所は記録してから結果を作る。`HeldByReservation` なら `OrderDispatchReservationConflictException`
     （従来の予約競合と同じ扱い）、`AlreadyCompleted` / 未定義値なら `InvalidOperationException`（見送りを主張しない）。
   - 接続確立の失敗は `Release` をやめ `MarkReservationForgone` にする。
4. `OrderApprovedHandler`: 抑止した再配送では**何も発行しない**（`Executed!` の参照より前に戻る）。
5. 追随（規則 9・10。下の母集合）: 誤りになるコメント・運用文書・テスト文書・IADR の追記。

**射程外**: 突合（`OrderReservationReconciler`）の `Release`（`NotPlaced`・門は既定で閉。見送りを発行しない）、
保護逆指値ガード・S1 実行器・エントリー直後の建玉解消の `Release`（いずれも自前の決済 DecisionId で、
次の巡回で撃ち直すのが設計。`OrderDispatchForgone` を発行しない）、`Reconciliation__ReleaseOnNotPlaced`（触らない）、
`Forgone` 行の保持期間パージ（**しない**。下の残余リスク）。

## 受け入れ基準

- AC1（否定形・最重要）: 接続確立の失敗で見送った承認が、OpenD の復帰後に重複配送されても**送信 0 回**（Close / Open）。
- AC2（否定形・最重要）: 予約前に見送った決済（建玉照会の不明・建玉なし）が、照会の回復後に重複配送されても送信 0 回。
- AC3: 抑止した再配送は**建玉照会もしない**・例外にならない・発行は 0 件（`OrderExecuted` も `OrderDispatchForgone` も）。
- AC4（否定形・Principle A）: 同じ DecisionId に `Reserved` の行（別の配送が発注中）があるとき、予約前の見送りは
  **見送りを主張しない**（`OrderDispatchForgone` を返さず予約競合の例外）。行は `Reserved` のまま。
- AC5: ストアの意味論（EF・InMemory で同一）: 無→`Recorded`、`Forgone`→`AlreadyForgone`（時刻を動かさない）、
  `Reserved`→`HeldByReservation`（`TryRecordForgone` は行を変えない）／`Recorded`（`MarkReservationForgone`）、
  `Completed`→`AlreadyCompleted`（行を変えない）。`Forgone` 行は `TryReserve` で取り直せず、`Release` で消えず、
  `FindStalledReserved` に載らず、`PurgeCompletedBefore` で消えない。
- AC6: 本番の `Program.cs` の組み立て（EF 実装）を通して、接続失敗 → 復帰後の再配送で送信 0 回。
  ストアの登録を外すとハンドラが解決できず赤になる。
- AC7（変えない側）: 見送りの理由・イベントの形・台帳の allowlist・突合の門（`ReleaseOnNotPlaced=false`）は不変。
  発注に成功した承認の再配送は従来どおり相 1 が既存結果を再発行する。

## テスト ID（予約済み T-10-823〜T-10-832）

| ID | 内容 |
| --- | --- |
| T-10-823 | AC1（Close / Open） |
| T-10-824 | AC2（照会不能 / 建玉なし） |
| T-10-825 | AC3（抑止した再配送は建玉照会もせず、見送りの記録も時刻も動かさない） |
| T-10-826 | 予約前の見送り 5 理由（到達させやすい全通り）が、見送りを発行する前に `Forgone` を記録する |
| T-10-827 | AC4（別の配送の `Reserved` がある DecisionId で見送りを主張しない） |
| T-10-828 | AC5（ストアの意味論・EF / InMemory の両実装で同一） |
| T-10-829 | `Completed` の行がある DecisionId で見送りを主張しない（`AlreadyCompleted`・未定義値の既定も同じ側） |
| T-10-830 | AC3 のハンドラ側（Wolverine の本番配線で、抑止した再配送の発行が 0 件・例外なし） |
| T-10-831 | AC6（本番の組み立て） |
| T-10-832 | AC7（接続失敗の見送りは従来どおり見送りイベントを 1 件発行し、行は `Forgone`・`BrokerOrderId` は null） |

## 是正・追随の母集合（規則 9・10。着手前に自分で引いた）

`OrderDispatchForgone` を**作る**箇所（テストを除く全 backend）:

```
$ git grep -n "OrderDispatchForgone(" -- backend/Services ':!*Tests*'
OrderExecutionAppService.cs:481   （唯一の生成点＝private Forgone）
$ git grep -n "return Forgone(" -- backend/Services/OrderExecutionService ':!*Tests*'
OrderExecutionAppService.cs:123 132 159 168 173 182 194 202 255   （9 箇所。255 だけが予約の後＝接続確立の失敗）
```

**9 箇所すべてを対象にする**（除外ゼロ）。台帳は 7 理由すべてを allowlist で `true`（在庫を戻す）にしているため、
どの見送りも同じ前提（その DecisionId はもう送られない）に依っている。

予約を `Release` する箇所:

```
$ git grep -n "reservations.Release(" -- backend ':!*Tests*'
OrderExecutionAppService.cs:253          → 対象（本件）
OrderExecutionAppService.cs:647          → 除外: エントリー直後の建玉解消の成行。DecisionId は決済用の派生 ID で、
                                            再配送はエントリーの ExecutionRecord で相 1 に止まる。見送りを発行しない
ProtectiveStopGuard.cs:411               → 除外: 次の巡回で撃ち直すのが設計（IADR-0117 改定 7）。見送りを発行しない
SoftwareStopExecutor.cs:266              → 除外: 同上（S1 の決済）。見送りを発行しない。#950 / #956 が触るファイル
OrderReservationReconciler.cs:155        → 除外: NotPlaced の解放。門は既定で閉（IADR-0362）。見送りを発行しない
```

`IOrderReservationStore` の実装（インタフェースへ口を足すと追随漏れは CS0535 でビルドが落ちる）:

```
$ git grep -n ": IOrderReservationStore" -- backend
EfOrderReservationStore.cs / InMemoryOrderReservationStore.cs
Tests/.../OrderExecutionServiceTests.cs:143 (FlakyCompleteReservationStore) / Tests/Hosted/OrderReservationRetentionServiceTests.cs:26 (RecordingStore)
```

誤りの側の文字列（「予約を解放」「予約は解放」「予約行を削除」）で全文書を走査し、**本件の経路（承認の発注の接続確立の失敗）を
述べているものだけ**を追随させる:

- 対象: `OrderExecutionAppService.cs`（冒頭と catch）、`MMApiMoomooTradeClient.cs:460`、
  `BrokerUnavailableException.cs`（受け手の列挙）、`OrderDispatchForgoneLifecycle.cs`（根拠の列挙と**行番号**——
  本 PR で行がずれて偽になる。規則 10）、`docs/tests/FR-10_risk-controls-tests.md` の T-10-302 / T-10-408、
  IADR-0211 決定 3(a) と IADR-0356 残余リスク 3（日付つき追記）。
- 除外（別の経路の記述で真のまま）: IADR-0117:218・IADR-0210:192・IADR-0344:82（ガード・S1 の撃ち直し）、
  IADR-0362・`ReconciliationOptions`・`OrderReservationReconciler`・運用手順の `NotPlaced` 解放、
  IADR-0240 / 通知サービス（報告書の版番号の予約で無関係）、T-10-409 / T-10-756（ガード）。

## 残余リスク（IADR-0398 に同じ）

- 見送りを記録した後、見送りイベントの発行より前にプロセスが落ちると、再配送は抑止されて**見送りイベントは出ない**
  （理由を記録していないため再発行できない）。台帳は承認を窓（30 分）のあいだ処理中として押さえる＝安全側
  （#852 以前の挙動）で、通知は抑止時の Warning ログだけになる。
- `Forgone` 行はパージしない（パージすると保持期間の後の再配送で穴が戻る）。行数は見送りの件数で増える。
