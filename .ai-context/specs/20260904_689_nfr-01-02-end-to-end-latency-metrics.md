---
title: NFR-01/02 の端点間レイテンシ計器を新設する（#689）
type: spec
status: draft
related_ids: [NFR-01, NFR-02, NFR-07, FR-02, FR-03, FR-04, FR-05, FR-11]
author: endazon (with Claude Code)
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
adr_refs:
  - IADR-0307
issue: "#689"
---

# 作業仕様書: NFR-01/02 の端点間レイテンシ計器を新設する（#689）

## 背景

計画の非機能要件表（`02_requirements/01_requirements.md`）は次の 2 件を持つ。

| ID | 区分 | 指標 | 目標 | 備考 |
| --- | --- | --- | --- | --- |
| NFR-01 | 性能 | 価格変動検知から発注完了までの所要時間 | 5 分以内 | LLM 判断時間を含む |
| NFR-02 | 性能 | 定時取引サイクル 1 回の所要時間 | 10 分以内 | 収集→判断→発注→記録 |

**この 2 件を測る計器は存在しない。** 既存の `ast.trade_cycle.decision_duration_ms`（#287 /
IADR-0255）は**判断 1 回**の所要しか測らず、しかも `TradeDecisionService` の 1 プロセス内で閉じている。
端点（起点イベントと発注／記録）は**サービスを跨ぐ**ため、単一サービス内の計器では原理的に測れない。

実測（本作業の着手時）:

```
$ grep -rn "NFR-01\|NFR-02" backend --include=*.cs
（0 件）
```

本 issue の範囲は**計器を入れるところまで**である。実 LLM を含む開場中の実測は #690 の持ち場であり、
本 PR では行わない（実環境不要）。

## 何が測れないのか（真因）

イベントの相関は 2 系統に分かれており、**起点イベントと注文チェーンが繋がっていない**。

| 系統 | 相関の鍵 | 事実 |
| --- | --- | --- |
| 市場・情報系 | `PriceMovementDetected.EventId` / `InformationCollected.EventId` | 起点。発生時刻を持つ |
| 注文系 | `DecisionId`（`TradeDecisionMade` → `OrderApproved` → `OrderExecuted`） | `TradeDecisionAppService` が `Guid.NewGuid()` で**新規採番**する |

`TradeDecisionService` は起点イベントを受け取って判断するが、**起点の素性（どちらの系統か・いつ起きたか）を
下流へ 1 バイトも渡していない**。監査台帳（`AuditService`）は両方のイベントを見ているが、相関 ID が
別物であるため、台帳の中でも結べない。**射影（IADR-0089 型）で足りるかを最初に検討したが、
射影を置いても同じ結線が要る**（詳細は IADR-0307 の選択肢比較）。

## 決めたこと（詳細は IADR-0307）

1. **起点の素性（cycle provenance）を注文チェーンへ載せる。** 契約イベント 3 本
   （`TradeDecisionMade` / `OrderApproved` / `OrderExecuted`）の**末尾へ既定値つきで 2 フィールド**を足す。
   - `string? CycleTrigger` —— `scheduled` / `price-movement`（既存のメトリクスタグ語彙をそのまま使う）
   - `DateTimeOffset? CycleStartedAt` —— 起点イベント自身の時刻（`CollectedAt` / `DetectedAt`）
2. **測定は 2 点。突き合わせ（join）はしない。** 起点時刻がイベントに載っているため、終点で
   引き算するだけで区間が閉じる（状態を持つ射影が要らない＝レプリカ・再起動に影響されない）。
   - NFR-01 の終点＝**発注完了**（`OrderExecutionService` が `OrderExecuted` を発行する点）
   - NFR-02 の終点＝**記録完了**（`AuditService` が監査台帳へ 1 行書いた点）
3. 🔴 **「測れなかった」と「0 だった」を区別する。** 起点が無い／経過が負のときは
   **ヒストグラムへ 1 件も入れず**、別の Counter（`ast.trade_cycle.latency_unobserved`）へ
   理由タグつきで 1 件数える。0 を入れると**目標を満たしているように見える**。
4. **休場の早期 return は計上しない。** 休場では判断が走らず `TradeDecisionMade` が出ないため、
   provenance を持つ注文が 1 件も生まれない＝**ヒストグラムにも未観測 Counter にも入らない**。
   「サイクルが 1 周した」とは数えない、という否定形を単体テストで固定する。
5. **ヒストグラムのバケット境界を明示する。** 既定境界の上限は 10,000 ms であり、
   5 分（300,000 ms）・10 分（600,000 ms）はすべて `+Inf` に落ちて分位点が意味を失う。
   OTel の View で境界を与え、**300,000 と 600,000 を境界そのものに置く**
   （超過件数がバケット差で直接読める＝分位点の補間に頼らない）。

## 変更対象

| # | ファイル | 変更 |
| --- | --- | --- |
| 1 | `backend/Shared/AiStockTrading.Shared.Contracts/Events/TradeDecisionMade.cs` | provenance 2 フィールド追加（末尾・既定 null） |
| 2 | 同上 `OrderApproved.cs` | 同上 |
| 3 | 同上 `OrderExecuted.cs` | 同上 |
| 4 | 同上 `Observability/BusinessMetricNames.cs` | 計器 3 本・タグ 1 本を追加 |
| 5 | 同上 `Observability/BusinessMetrics.cs` | 記録メソッド 2 本・未観測理由の語彙を追加 |
| 6 | `backend/Services/TradeDecisionService/Features/TradeDecision/DecisionTrigger.cs` | 起点時刻を持たせる |
| 7 | 同 `.../DecideTrade/TradeDecisionAppService.cs` | `TradeDecisionMade` 生成 2 箇所へ provenance を載せる |
| 8 | 同 `Infrastructure/Steps/InformationCollectedHandler.cs` | 起点時刻（`CollectedAt`）を供給 |
| 9 | 同 `Infrastructure/Steps/PriceMovementDetectedHandler.cs` | 起点時刻（`DetectedAt`）を供給 |
| 10 | `backend/Services/RiskManagementService/Features/RiskManagement/OrderScreeningService.cs` | provenance を `OrderApproved` へ引き継ぐ |
| 11 | `backend/Services/OrderExecutionService/Features/OrderExecution/DispatchApprovedOrder/OrderExecutionAppService.cs` | provenance を `OrderExecuted` へ引き継ぐ（2 箇所） |
| 12 | 同 `Infrastructure/Steps/OrderApprovedHandler.cs` | **NFR-01 の計上点** |
| 13 | `backend/Services/AuditService/Infrastructure/Steps/AuditEventHandlers.cs` | **NFR-02 の計上点** |
| 14 | `backend/TestSupport/.../Foundation/Extensions/ObservabilityExtensions.cs` | ヒストグラムの View（バケット境界） |
| 15 | `deploy/observability/dashboards/ai-stock-trading-business.json` | パネル追加（検査器 R2 が全計器の被参照を要求する） |
| 16 | `docs/observability/observability.md` | 計器表へ追記 |
| 17 | `backend/Shared/AiStockTrading.Shared.Contracts.Tests/event-schemas.baseline.json` | 再生成（`UPDATE_EVENT_BASELINE=1`） |

## 引いた母集合と、除外したものとその理由

**軸 1（イベント生成点）**: `grep -rn "new TradeDecisionMade(\|new OrderApproved(\|new OrderExecuted("` を
本番コード（`/Tests/` 以外）へ掛けた。実測 8 箇所。

| 生成点 | provenance | 理由 |
| --- | --- | --- |
| `TradeDecisionAppService` ×2（決済・新規建て） | **載せる** | `DecisionTrigger` が起点を知っている |
| `OrderScreeningService`（判断由来の承認） | **載せる** | `TradeDecisionMade` から引き継ぐ |
| `PositionCloseService`（owner 手仕舞い） | **載せない（null）** | 取引サイクル起点ではない。人手の決済であり NFR-01/02 の対象外 |
| `MaintenanceMarginReductionService`（自動縮小） | **載せない（null）** | 同上。統制側の自動縮小であり収集・判断を経ていない |
| `OrderExecutionAppService` ×2（新規・冪等再発行） | **引き継ぐ** | 承認が持っていれば載せる |
| `OrderFillPoller`（約定追跡の後追い） | **載せない（null）** | `OrderApproved` を持たない経路。**発注完了より後**の状態遷移であり終点ではない |

null の 4 経路は**未観測 Counter で数える**（黙って消さない）。

**軸 2（計器レジストリの利用者）**: `BusinessMetricNames` を引く資産を
`grep -rln "BusinessMetricNames\|ast\.trade_cycle" scripts/ deploy/ .github/` で引いた。実測 4 件
（`scripts/check-observability-assets.js` / `scripts/README.md` / `scripts/scripts.repo.test.js` /
`deploy/observability/README.md`）。**検査器の R2 が「レジストリの各計器が少なくとも 1 パネルから
引かれること」を要求する**ため、ダッシュボードのパネル追加は任意ではなく必須である。

**除外**: `Tests/AiStockTrading.IntegrationTests`（実コンテナ E2E）は **provenance を載せない**。
既存の E2E は `OrderApproved` を直接投入しており、起点イベントを持たない。ここへ provenance を
足すと「計器が動くこと」ではなく「テストが自分で用意した値を読み返すこと」しか確かめられない。
端点間の結線は単体テストで固定する（本 issue は実環境不要が前提）。

## 受け入れ基準（issue #689）

- [ ] NFR-01（`PriceMovementDetected` → `OrderExecuted`）の端点間メトリクスが記録される
- [ ] NFR-02（`InformationCollected` → 記録完了）の端点間メトリクスが記録される
- [ ] **否定形**: 休場の早期 return ではサイクル完了として数えない
- [ ] **否定形**: 起点不明（provenance なし）で 0 を記録しない。未観測として別計器へ出る
- [ ] 単体テストで検証できる（実 LLM・実環境不要）
- [ ] 起点 ID コメント（NFR-01, NFR-02）付き

## 検証

```
dotnet build backend/backend.slnx
dotnet test backend/Shared/AiStockTrading.Shared.Contracts.Tests/...
dotnet test backend/Services/{TradeDecision,OrderExecution,RiskManagement,Audit}Service/Tests/...
dotnet format backend/backend.slnx --verify-no-changes
node scripts/check-observability-assets.js
node scripts/check-trace-blocks.js && node scripts/check-doc-links.js
node scripts/check-cross-repo-refs.js && node scripts/gen-knowledge-graph.js --check
COMMIT_RANGE=origin/develop..HEAD node scripts/check-adr-index-sync.js
node scripts/check-commit-messages.js
```

## 計画への環流

- 差異: **なし**。NFR-01/02 の端点は計画本文の語（「価格変動検知から発注完了まで」「収集→判断→発注→記録」）
  をそのまま終点に採った。目標値（5 分・10 分）は触っていない。
- 気付いた点（本 PR では起票しない）: 目標未達が実測で出た場合の扱いは #637 が「値を勝手に緩めず
  計画へ環流して裁定を得る」と定めている。本 PR は計器だけであり、判定はしない。
