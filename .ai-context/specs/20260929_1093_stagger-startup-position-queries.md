---
title: 起動直後の建玉照会を常駐ごとにずらす（スナップショット・稼働 probe の初回を遅らせ、ガードは即時のまま。#1093 段 2）
type: spec
status: accepted
related_ids: [FR-10, FR-20, UC-02, IADR-0459, IADR-0458, IADR-0144, IADR-0118, IADR-0150, IADR-0210, IADR-0211, IADR-0397, IADR-0412]
author: claude (Claude Code)
created: 2026-09-29
updated: 2026-09-29
plan_refs: []
---

# 起動直後の建玉照会をずらす（#1093 段 2）

## 背景

#1093 の段 1（IADR-0458・作業仕様書 `20260929_1093_guard-position-query-retry`）は、ガードの巡回の先頭の建玉照会に限って、分類できた一時的な失敗を 1 回だけ照会し直すようにした。
段 1 が残余として残したのは、**起動直後の照会の集中**である。発注執行サービスが再起動すると、次の 3 つの常駐が同じ瞬間に初回の建玉照会（`TrdGetPositionList`。対応市場ごとに 1 回＝US・JP の 2 回）を送る。

- 保護逆指値ガード（`ProtectiveStopGuardService`。30 秒ごと。Active な保護記録があるときだけ照会する）
- 建玉スナップショット（`BrokerPositionSnapshotService`。10 分ごと）
- 稼働 probe（`BrokerAvailabilityProbeService`。5 分ごと。`IsOperationalAsync` が `GetPositionsAsync` を流用する）

OpenD の頻度制限は 30 秒あたり 10 回程度で、失敗した照会も枠を消費する（IADR-0144 決定 5）。PoC では再起動を重ねた直後に頻度制限へ当たった。

起点は FR-10（保護逆指値）である。FR-20（Stage 1 の稼働の数え）は、稼働 probe の初回を遅らせることの影響先として確かめる。計画 ADR の新たな制約は無い。決定は IADR-0459 に記す。

## 決定の要約（IADR-0459）

- スナップショットと稼働 probe の構成に `InitialDelaySeconds`（初回の遅延・秒）を足し、`InitialDelay`（`TimeSpan`）で読む。
  - 既定: 稼働 probe **10 秒**、スナップショット **20 秒**。揺らぎは掛けない（固定値）。
  - 負の値は 0（遅らせない）に収める。巡回間隔を超える値は巡回間隔に収める。
- `ExecuteAsync` の中で、有効判定と開始ログの後、最初の巡回の前に 1 回だけ待つ。停止要求で取り消されたら、巡回を 1 回も回さずに正常に終わる。
- ガードは遅らせない（保護が先）。
- 待ちは、両常駐が既に受けている `TimeProvider` で行う（`Task.Delay(delay, timeProvider, token)`）。試験はタイマーを手で発火させる `TimeProvider`（`Tests/Hosted/ManualTimerTimeProvider.cs`）を渡し、壁時計を使わない。
  - 段 1 の `PositionQueryRetry` のような待ちの関数（`Func<TimeSpan, CancellationToken, Task>`）の注入は採らない。常駐のコンストラクタへ省略可能な依存を足すと、本番の組み立ての検査（`CompositionWiringGuardTests`・IADR-0397 の W1「省略可能依存の未解決」）が所見として赤にするためである。

## 母集合（規則 9: 起動直後に OpenD へ照会する経路を全部挙げ、範囲の内外を決める）

### 建玉照会の呼び出し元

`git grep -n "GetPositionsAsync\|QueryPositionsAsync" -- 'backend/**/*.cs' ':!*Tests*' ':!backend/Tests'` で走査した（2026-09-29・origin/develop e05e2c7c）。

| 呼び出し元 | 起動時に走るか | 本段の扱い |
| --- | --- | --- |
| `ProtectiveStopGuard.RunOnceAsync`（`ProtectiveStopGuardService` の巡回の先頭） | 走る（起動直後に 1 巡回目。Active な記録があるときだけ照会） | **遅らせない**（保護が先。IADR-0459 決定 2） |
| `ProtectiveStopGuard.HoldUnlessPositionGoneAsync`（建玉 0 の確かめ） | 巡回の中で条件つき | 範囲外（ガードの巡回に含まれる。遅らせない） |
| `BrokerPositionSnapshotService.PublishOnceAsync` | 走る（起動直後に 1 回目） | **遅らせる**（既定 20 秒） |
| `MoomooBrokerAdapter.IsOperationalAsync`（`BrokerAvailabilityProbeService.ProbeOnceAsync` から） | 走る（起動直後に 1 回目） | **遅らせる**（既定 10 秒） |
| `OrderExecutionAppService`（決済ゲート・S1 の武装。137 行・547 行） | 走らない（承認の受信が契機。時刻は外から決まる） | 範囲外（発注の経路を遅らせない。IADR-0211・IADR-0458 決定 4） |
| `SoftwareStopExecutor`（S1 の決済） | 走らない（価格の到達が契機） | 範囲外（同上） |
| `ProtectiveStopDriftAdopter`（乖離の取り込み） | 走らない（人の操作が契機） | 範囲外 |
| `PositionQueryRetry.QueryAsync`（段 1） | ガードの巡回の中 | 範囲外（ガードの一部） |
| `MoomooBrokerAdapter.GetPositionsAsync` / `QueryPositionsAsync`・`MMApiMoomooTradeClient.GetPositionsAsync` | 実装側（呼び出し元ではない） | 対象外 |

`RiskManagementService` の `OrderDispatchForgoneLifecycle.cs` の 2 件はコメントの中の語であり、呼び出しではない（除外）。

### 起動直後に OpenD へ照会する他の常駐（建玉以外）

`grep -n "AddHostedService" backend/Services/OrderExecutionService/Program.cs` で発注執行サービスの常駐を全部挙げた。

| 常駐 | 起動時の OpenD 照会 | 本段の扱い |
| --- | --- | --- |
| `OrderFillPollingService`（30 秒） | 非終端の発注結果があるときだけ、注文の照会（`TrdGetOrderList` / `TrdGetHistoryOrderList`） | **範囲外**。建玉照会とは別の照会で、非終端の記録が無ければ照会は 0 回。遅らせると約定の台帳への到達（統制の入力）が遅れる |
| `OrderReservationReconciliationService`（配備の既定 1 時間） | 滞留（既定 2 時間以上）した Reserved の予約があるときだけ、注文の照会 | **範囲外**。滞留の予約は通常 0 件で、照会も建玉照会ではない |
| `OrderReservationRetentionService` | 照会しない（DB のみ） | 対象外 |
| `BrokerAvailabilityProbeService` の口座種別の観測（`GetAccountStateAsync`） | 到達できた巡回で口座の照会（`TrdGetAccList` / `TrdGetFunds`） | **遅らせる側に含まれる**（同じ巡回の中なので、probe の初回と一緒にずれる） |

他のサービス（市場監視など）が使う OpenD の相場系の照会は、別の口（相場）であり本段の範囲外とする。

### 「照会は 1 巡回 1 回」「初回は即時」を前提にした記述

`git grep -n "初回" -- backend/Services/OrderExecutionService/Hosted backend/Services/OrderExecutionService/Features/OrderExecution/ObserveBrokerPositions backend/Services/OrderExecutionService/Features/OrderExecution/ObserveBrokerAvailability` を走査した。初回の即時性を前提にしたコメント・試験は見つからなかった。

### 配備の構成（helm）

`grep -rn "Reconciliation__Positions\|Stage1__UptimeProbe" deploy/` は 0 件である。両節とも配備はアプリの既定に委ねている（`values.yaml` / `values-local.yaml` とも該当キーなし）。**既存の流儀に従い、values には足さない**（既定値で足りる。変えたいときは `Reconciliation__Positions__InitialDelaySeconds` / `Stage1__UptimeProbe__InitialDelaySeconds` で上書きできる）。`appsettings*.json` にも両節は無い。

### 文書

`grep -rln "UptimeProbe\|Reconciliation:Positions" docs/` は `docs/functional/FR-10_risk-controls.md`（Enabled=false の言及）と `docs/operations/broker-execution-paths-runbook.md`（同）の 2 件で、いずれも初回の時刻に触れていない。追随は不要とした。

## FR-20（Stage 1 の稼働の数え）への影響の確かめ

`RiskManagementService/Domain/Stage1SessionUptime.cs` の `Credit` を読んだ。

- 観測は分単位（米国東部時間の分）で積む。前回の観測から今回の観測までの間隔が、観測が主張する区間（probe の巡回間隔。既定 5 分）以内のときだけ、その間を稼働として積む。**その日の最初の観測は 0 分**（遡らない）。
- 初回を 10 秒遅らせると、再起動をはさむ区間（再起動前の最後の観測 → 再起動後の最初の観測）が 10 秒長くなる。区間が 5 分を超えれば、その区間は積まれない。
  - **向きは主に積み不足（安全側）。** 区間が 5 分以内に収まる場合は遅延の分だけ区間が広がる（分へ切り捨てて最大 1 分。その間もプロセスは動いており、後続の観測も同じだけずれて引けの窓で相殺される。監査の指摘で精度を上げた）。 最悪で再起動 1 回につき 1 区間（最大 5 分）を失う。1 日の判定の閾値（通常日 195 分／半日 105 分）に対して、再起動を日に何度も重ねない限り影響しない。
  - 再起動をはさまない区間（2 回目以降の巡回）は、初回と同じだけずれるので、間隔は変わらない。
- 既存の試験（`Stage1SessionUptimeTests`・`BrokerAvailabilityProbeServiceTests`）は観測の積み方と 1 巡回を固定しており、初回の時刻に依存しない（本段の変更後も全件成功。下の検証）。

## 受け入れ基準 → 試験

| 受け入れ基準 | 試験（テスト ID） |
| --- | --- |
| スナップショットは、初回の建玉照会の前に既定 20 秒待つ。待ちの間は照会しない | `BrokerPositionSnapshotServiceTests.T_10_1742_初回の照会の前に既定の遅延だけ待つ`（T-10-1742） |
| 稼働 probe は、初回の probe の前に既定 10 秒待つ。待ちの間は照会しない | `BrokerAvailabilityProbeServiceTests.T_10_1743_初回のprobeの前に既定の遅延だけ待つ`（T-10-1743） |
| 待ちの間に停止要求が来たら、1 回も照会せずに正常に終わる（例外で終わらない・エラーを記録しない） | 各 `T_10_1744_初回の遅延の間に停止すると一度も照会せず正常に終わる`（T-10-1744） |
| 負の設定は遅らせない（待たずに初回を回す）。巡回間隔を超える値は巡回間隔に収める。未設定は既定 | 各 `T_10_1745_初回の遅延の設定を収める`（Theory）・各 `T_10_1745_負の遅延なら待たずに初回を回す`（T-10-1745） |
| ガードは遅らせない（起動直後に 1 巡回目を回す） | `ProtectiveStopGuardServiceTests.T_10_1746_ガードは起動直後に最初の巡回を回す`（T-10-1746） |

試験 ID は T-10-1742 から採番した（T-10-1740・T-10-1741 は別の PR が予約している）。テスト仕様書 `docs/tests/FR-10_risk-controls-tests.md` の末尾に節を足す。

### 自己変異の確認（完了前に実施）

- スナップショットの初回の待ちを外す → T-10-1742・T-10-1744 が落ちること。
- 稼働 probe の初回の待ちを外す → T-10-1743・T-10-1744 が落ちること。
- ガードに 10 秒の初回の待ちを入れる → T-10-1746 が落ちること。
- 負の値の収め（`Math.Max(0, …)`）を外す → T-10-1745 が落ちること。

## 検証（2026-09-29・基準 origin/develop e05e2c7c）

- `dotnet build backend/backend.slnx`: `Build succeeded. 0 Warning(s) 0 Error(s)`。
- `dotnet test backend/Services/OrderExecutionService/Tests`: `Passed! - Failed: 0, Passed: 1211, Skipped: 0, Total: 1211`。
- `dotnet format backend/backend.slnx --verify-no-changes --include backend/Services/OrderExecutionService/`: 差分なし（exit 0）。
- `node scripts/check-test-traceability.js`: exit 0（採番の最大値 T-10-1746・baseline の増加なし）。
- `node scripts/check-trace-blocks.js`: exit 0（53 件に違反なし）。
- `node scripts/check-doc-links.js`: exit 0（1015 件に破損なし）。
- `node scripts/check-adr-index-sync.js`: exit 0（変更された実装ADR 1 件の索引行も変更済み）。
- `node scripts/gen-knowledge-graph.js --check`: exit 0（in-repo 参照に実在しないものなし）。
- `node scripts/check-commit-messages.js`: exit 0（1 件が規約に適合）。
- 自己変異（上の 4 通り）: いずれも該当の試験が落ち、戻して成功した。
  - スナップショットの待ちを外す → T-10-1742・T-10-1744（スナップショット）が失敗。
  - 稼働 probe の待ちを外す → T-10-1743・T-10-1744（稼働 probe）が失敗。
  - ガードに 10 秒の待ちを入れる → T-10-1746 と既存のガードの常駐の試験 3 件が失敗。
  - 負の値の収めを外す（`Math.Clamp(…, 0, …)` → `Math.Min(…)`）→ 各常駐の T-10-1745 が 2 件ずつ失敗。

## 残余

- **ずらしても 30 秒の窓の中の要求数は減らない。** 固定値でずらすことが減らすのは同じ瞬間の集中（新しく張った接続への同時の往復）と、ガードの照会し直し（段 1。待ち 1〜3 秒・5〜15 秒）の窓と probe・スナップショットの初回との重なりの一部である。頻度制限の窓の数え方（滑る窓か固定窓か、口ごとか口座ごとか）は実測していない。
- 再起動を 30 秒以内に何度も重ねる操作では、前の起動の照会が窓に残るので、ずらしだけでは防げない。
- 稼働 probe の初回の遅延は、Stage 1 の稼働の数えを再起動 1 回につき最大 1 区間（5 分）減らし得る（積み不足の側。上の確かめ）。
- 実クラスタ（OpenD）での実測は未実施。

## 監査の指摘への対応（2026-09-29）

フレッシュな文脈の監査の判定は GO（🔴 なし）。変異 12 件はすべて検出された。文書の精度の指摘を反映した。
- 🟡: IADR-0459 決定 3 に「頻度制限の後の照会し直し（5〜15 秒）は probe の初回と重なり得る」を足した（索引の行も）。
- 🟢: FR-20 の影響を「積み不足の側。区間が巡回間隔に収まる場合の広がりは分の切り捨てで最大 1 分」と正確にした（IADR・本書・構成の注記・テスト仕様書）。
- 🟢: durable キューに溜まったメッセージが起動直後に配送される発注の経路を、IADR の残余に足した。
- claude-review の 🟢: `InitialDelay` の収めの Theory を `PositionReconciliationOptionsTests` へ移した（`Interval` の収めの試験と同じ場所）。
