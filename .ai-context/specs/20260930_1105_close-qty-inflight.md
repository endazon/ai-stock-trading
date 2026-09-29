---
title: 決済の数量から、同じ建玉を売っている処理中の決済を引く（S0 / S3 の保護レグは引かない。#1105）
type: spec
status: accepted
related_ids: [FR-10, FR-05, UC-06, IADR-0461, IADR-0355, IADR-0211, IADR-0458, IADR-0356, IADR-0398, IADR-0344, IADR-0210, IADR-0113, IADR-0406]
author: claude (Claude Code)
created: 2026-09-30
updated: 2026-09-30
plan_refs: []
---

# 決済の数量から処理中の決済を引く（#1105）

## 背景（PoC・2026-09-29 UTC の実測）

AAPL 1,428 株を保有していた（S1 の保護記録 2 件: 713 株 @331.67・715 株 @330.88）。

| 時刻（UTC） | 事象 |
| --- | --- |
| 15:13:28 | 取引判断が Sell 1,428（指値）を決めた |
| 15:13:53 | S1 が 713 株の成行決済を送った（Accepted） |
| 15:13:56 | 判断の決済 1,428 株が moomoo に「Not enough positions」で拒否された |
| — | 715 株が残った |

## 原因（origin/develop 87c40e1f で確認）

- 決済のゲート（`OrderExecutionAppService.ExecuteCoreAsync` の建玉照会 → `BrokerHeldPositionGate.Evaluate`）は、ブローカーの `Position.Qty`
  （`MMApiMoomooTradeClient` → `MoomooBrokerAdapter.GetPositionsAsync`）を数える。この値は**約定していない売り注文が押さえている株数を引かない**。
- ゲートは発注執行が自分で出した**処理中の決済**（非終端の Close の `ExecutionRecord`）も見ない。S1 の決済は非終端の Close として保存される
  （`SoftwareStopExecutor`）。
- 🔴 非終端の Close には **S0 / S3 のブローカー側の保護レグ**も含まれる（`PlaceProtectiveStopAsync` と保護逆指値ガードの張り直しが Close として保存する）。
  これを引くと、S0 の建玉ではすべての判断の決済が 0 株になる＝FR-10「手仕舞いは止めない」に反する。**引いてはならない。**

## 決定の要約（IADR-0461。IADR-0355 決定2 の拡張）

1. ブローカーの建玉照会でゲートが数量を決めた**直後**・予約（相 2）の**前**に、処理中の決済の株数を数える。
   - 対象: 発注執行の非終端の Close の記録のうち、同じ（銘柄・市場・決済の方向）のもの。
   - 除外: 今の承認（同じ DecisionId）の記録。Active な S0 / S3 の保護記録（`!IsSoftwareStop`）の逆指値レグ
     （記録の OrderId が保護記録の `StopOrderId` と一致、または記録の DecisionId が保護記録の `StopDecisionId` と一致）。
   - 🔴 **ブローカーが「まだ生きている」と答えた注文だけを数える**（`IBrokerAdapter.GetOrderAsync`。下の「窓」）。株数は
     記録の数量 − max(記録の約定数, 照会の約定数)。照会が null・例外・終端なら数えない。
2. 送る数量 ＝ min(ゲートの後の数量, 決済方向の建玉 − 処理中)。
   - 0 以下 → **予約の前に見送る**。理由は新設の `InFlightCloseCoversPosition`（序数 7）。
   - 縮んだが 1 以上 → 縮めて送る。Warning ログと、新設の事実 `CloseReducedForInFlightCloses`（監査台帳だけが購読）を残す。
     **乖離イベント（`PositionReconciliationDrift`）は出さない**（台帳の乖離ではない）。
3. 建玉照会の能力が無い発注先（内蔵 paper）では起きない（IADR-0355 決定4。`brokerPositions` が null なら分岐に入らない）。
4. 拒否の後の自動の出し直しは足さない（IADR-0211 決定3・IADR-0458 決定4）。プロンプトは変えない。

## 母集合（規則 9: 誤りの側の文字列で引いた）

### 決済（Close）を発注する経路（`new ExecutionRecord(` / `PlaceOrderAsync|PlaceMarketOrderAsync|PlaceStopOrderAsync` で走査）

| 経路 | 場所 | 本ゲートを通るか | 処理中の決済として数えるか |
| --- | --- | --- | --- |
| 判断の決済（`OrderScreeningService` が承認） | `OrderExecutionAppService.ExecuteCoreAsync` | **通る**（本是正の対象） | 数える（別の DecisionId なら） |
| 利用者の手仕舞い（`PositionCloseService`） | 同上 | **通る** | 数える |
| 維持率割れの自動縮小（`MaintenanceMarginReductionService`） | 同上 | **通る** | 数える |
| S1 の成行決済 | `SoftwareStopExecutor`（`SoftwareCloseDecisionId`） | 通らない（自分の残保護数量で送る） | **数える**（本件の実測） |
| 失効した S0 の成行手仕舞い | `ProtectiveStopGuard`（`CloseDecisionId`） | 通らない | 数える |
| 保護逆指値が成立しないときの建玉解消 | `OrderExecutionAppService.CloseUnprotectedPositionAsync` | 通らない | 数える |
| **S0 / S3 の保護レグ（同時発注）** | `OrderExecutionAppService.PlaceProtectiveStopAsync` | 通らない | 🔴 **数えない**（Active な保護記録の `StopOrderId` / `StopDecisionId` で除く） |
| **S0 / S3 の保護レグ（張り直し）** | `ProtectiveStopGuard`（`StopDecisionId(entry, attempt)`） | 通らない | 🔴 **数えない**（張り直しで保護記録の `StopOrderId` / `StopDecisionId` が更新される） |
| 突合が採用した発注（`OrderReservationReconciler.BuildRecord`） | 突合 | 通らない | 採用した注文の種類に従う（保護レグの採用はガードが `StopOrderId` を書くので除かれる） |
| 乖離の取り込み（`ProtectiveStopDriftAdopter`） | 発注執行 | 発注しない（保護の主張を減らすだけ） | 対象外 |

本ゲートを通る経路は承認（`OrderApproved`）の 1 本だけであり、上の 3 起点すべてが同じ位置を通る。

### 見送り理由（`OrderDispatchForgoneReason`）の消費者（`OrderDispatchForgoneReason` で全文を走査）

| 消費者 | 場所 | 対応 |
| --- | --- | --- |
| 在庫解放の allowlist | `RiskManagementService/Domain/OrderDispatchForgoneLifecycle.cs` | **`true` で足す**（予約の前に return する＝確実に未発注） |
| 要素数のトリップワイヤ | `RiskManagementService/Tests/Domain/OrderDispatchForgoneLifecycleTests.cs` | 7 → 8。肯定形 Theory に足す |
| 処理中から外れる被覆 | `RiskManagementService/Tests/Infrastructure/Steps/PortfolioLedgerConsumersTests.cs` | 足す |
| 否定形の番兵 | `RiskManagementService/Tests/Domain/UndefinedForgoneReasons.cs` | 触らない（列挙から導く） |
| 通知の文言 | `NotificationService/Features/Notifications/NotificationFormatter.cs` `ReasonLabel` | 文言を足す。重大度は Warning のまま |
| 序数の固定 | `Shared.Contracts.Tests/StopLossMethodContractTests.cs` | 序数 7 を固定する |
| メトリクスのタグ | `BusinessMetrics.RecordOrderDispatchForgone` | 列挙名をそのまま使う（変更不要） |
| 監査 | `AuditEntryFactory.From(OrderDispatchForgone)` | 理由をそのまま書く（変更不要） |
| 見送りの発行 | `OrderApprovedHandler` | 理由を問わず同じ（変更不要） |

### 新設の事実（`CloseReducedForInFlightCloses`）を足すと動く検査

`EventTypeDiscovery` の母集合に入るため、次が赤くなる／揃える:
`EventBackwardCompatibilityTests`（基準 `event-schemas.baseline.json` に追加）・`EventMessageTypeNameTests`（識別子を固定）・
`AuditConsumerCoverageTests`（監査ハンドラ）・`AuditCycleCompletenessTests`（`AuditEntryFactory.From` の写像）・`AuditPayloadSecretExposureTests`。

### 規則 10（この変更で新たに誤りになる自分の記述）

- 機能仕様書 FR-10 の「決済は発注の直前にブローカーの実建玉と突き合わせる」の表は、送る数量を「実建玉の数量」とだけ書いている → 処理中の決済を引く段を足す。
- `OrderDispatchForgoneLifecycle` の注記「現行 7 値」 → 8 値。
- `docs/api/events-and-ports.md`・`docs/data/audit-events.md` に新しい事実の行を足す。

## 窓（規則 11）

窓は「ブローカーの建玉照会」と「処理中の決済の読み取り」の間である（照会は OpenD への往復）。

プローブ（試験で実測した。T-10-1761・T-10-1762）:

- **増える側**: 建玉照会の最中に S1 の決済 713 株（未約定）が保存される。建玉は 1,428。正しい送る数量は 715。
- **減る側**: 建玉照会の最中に処理中の S1 の決済 713 株が約定し、建玉は 715 に減る（記録の更新はポーラーの 30 秒の遅れがあり得る）。正しい送る数量は 715。

| 形 | 増える側 | 減る側 |
| --- | --- | --- |
| 前の端だけ（照会の**前**に処理中を読む） | ✗ 1,428 を送る（拒否。是正前と同じ） | ✗ 2 株しか送らない（713 株を取り残す。是正前より悪い） |
| 後の端だけ（照会の**後**に処理中を読み、ブローカーに生きているかを確かめる）＝**採用** | ✓ 715 | ✓ 715 |
| 両端（max(前, 後)） | ✓ 715 | ✗ 2 株 |

採用した形の誤りは「照会の後・読み取りの前の数百ミリ秒に約定し、建玉照会がそれを映していない」ときに**是正前と同じ拒否**になるだけで、
是正前より悪い側（取り残し）へは倒れない。

🔴 **ブローカーへの確かめ（`GetOrderAsync`）を省くと、減る側がポーラーの遅れ（既定 30 秒）のあいだ 2 株になる**（自己変異で実測。下の表）。
確かめられない（null・例外）ときは数えない＝是正前と同じ（拒否され得る）側へ倒す。

## 試験（`OrderExecutionServiceCloseInFlightTests`）

| ID | 内容 |
| --- | --- |
| T-10-1752 | 1,428 保有・S1 の決済 713（Accepted・未約定）・承認 1,428 → 715 を送る。乖離は出さない。縮めた事実が付く |
| T-10-1753 | 処理中が建玉をすべて覆う → `InFlightCloseCoversPosition` で見送り。予約は Forgone。発注 0 |
| T-10-1754 | 🔴 Active な S0 の保護レグ（全量・非終端の Close）→ 引かない。全量を送る |
| T-10-1755 | 🔴 同上・S3 |
| T-10-1756 | 部分約定（713 のうち 300 約定）→ 413 を引く（ブローカーの約定数も記録の約定数も見る） |
| T-10-1757 | 終端の記録は引かない（確かめの照会もしない） |
| T-10-1758 | 別の銘柄・別の市場・逆方向は引かない |
| T-10-1759 | 同じ DecisionId の再配送は既存結果の再発行（送らない・照会しない） |
| T-10-1760 | 建玉照会の能力が無い発注先（内蔵 paper）では分岐に入らない |
| T-10-1761 | 台帳の乖離（Reduce）と処理中の決済が重なる → 乖離の数量は変わらない。送るのは両方を引いた数量 |
| T-10-1762 | 窓: 増える側・減る側のプローブ |
| T-10-1763 | ブローカーが生きていると確かめられない処理中（null・例外・終端）は引かない |
| T-10-1764 | 保護記録ストアが無い構成では引かない（保護レグを見分けられない） |
| T-10-1765 | 記録ストアの問い合わせ（EF・インメモリ）: 非終端の Close を銘柄・市場・方向で絞る |
| T-10-1766 | 見送り理由の消費者（allowlist・通知の文言・序数）・監査の写像 |

自己変異:

| 変異 | 落ちる試験 |
| --- | --- |
| S0 / S3 の除外を外す | T-10-1754（補足の 2 件を含む）・T-10-1755（4 件赤） |
| 処理中を引く処理を外す | T-10-1752（単体・本番配線）・T-10-1753・T-10-1754 の補足・T-10-1756・T-10-1759・T-10-1761・T-10-1762（増える側）（12 件赤） |
| ブローカーへの確かめを外す（記録の値だけを使う） | T-10-1756（記録 0・ブローカー 300）・T-10-1762（減る側・照会の前）・T-10-1763（3 件）（5 件赤） |
| 処理中を建玉照会の**前**に読む（前の端） | T-10-1762 の増える側・減る側（照会の最中）（2 件赤） |
| 前と後の両方を読み大きい方（両端） | T-10-1762 の減る側（照会の最中）（1 件赤） |

## 残余リスク

- 🔴 **S0 のブローカー側の逆指値そのものが、実弾でブローカーの売れる数量を押さえるか**は未確認（planning#704）。押さえるなら、S0 の建玉への判断の決済は
  是正の前後を問わず拒否され得る。本是正は S0 / S3 の保護レグを**引かない**（引くと SIMULATE で決済が常に 0 になる）。
- 送信の結果が不明な処理中の決済（予約が Reserved のまま・記録が無い）は数えない（記録が無いため）。拒否され得る（是正前と同じ）。
- 訂正（`OrderAmendmentService`）で数量を変えた注文は、記録の数量が古いまま（ブローカーの照会は数量を返さない）。
- 保護記録が Active でなくなった後も非終端のまま残る保護レグは引かれる（ブローカーで生きているなら売れる数量を押さえているので、引く側が正しい）。
- 照会の後・読み取りの前に約定し、建玉照会がそれを映していない数百ミリ秒の窓では拒否され得る（是正前と同じ）。
- S1 の決済・失効した S0 の成行手仕舞いの側は、判断の決済が処理中であることを見ない（本是正の範囲外。拒否されたら各経路の既存の扱い）。
- 処理中の注文 1 件ごとに OpenD へ注文照会を 1 回送る（処理中が無い通常時は送らない）。

## 検証

```
dotnet build backend/backend.slnx
dotnet test backend/Services/OrderExecutionService/Tests/OrderExecutionService.Tests.csproj
dotnet test backend/Services/RiskManagementService/Tests/RiskManagementService.Tests.csproj
dotnet test backend/Services/NotificationService/Tests/NotificationService.Tests.csproj
dotnet test backend/Services/AuditService/Tests/AuditService.Tests.csproj
dotnet test backend/Shared/AiStockTrading.Shared.Contracts.Tests/AiStockTrading.Shared.Contracts.Tests.csproj
dotnet format backend/backend.slnx --verify-no-changes
node scripts/check-test-traceability.js
node scripts/check-trace-blocks.js
node scripts/check-doc-links.js
node scripts/check-adr-index-sync.js
node scripts/gen-knowledge-graph.js --check
node scripts/check-cross-repo-refs.js
node scripts/check-plan-id-qualification.js
```

結果（2026-09-30・手元で実行）:

| コマンド | 結果 |
| --- | --- |
| `dotnet build backend/backend.slnx --no-incremental` | 0 Warning(s) / 0 Error(s) |
| OrderExecutionService.Tests | Passed 1243 / Failed 0 |
| RiskManagementService.Tests | Passed 2085 / Failed 0 |
| NotificationService.Tests | Passed 850 / Failed 0 |
| AuditService.Tests | Passed 244 / Failed 0 |
| AiStockTrading.Shared.Contracts.Tests | Passed 514 / Failed 0 |
| `dotnet format backend/backend.slnx --verify-no-changes` | 差分なし |
| 文書・トレーサビリティの検査器 | PR 本文に貼る |
