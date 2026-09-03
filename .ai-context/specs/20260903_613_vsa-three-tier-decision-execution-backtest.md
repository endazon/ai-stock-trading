---
title: #613 第5弾 Features/<集約>/<操作>/ の 3 段化 —— TradeDecision / OrderExecution / Backtest の移送
type: spec
status: draft
related_ids:
  - NFR
  - IADR-0259
  - IADR-0276
  - IADR-0289
  - MSP:ADR-0065
  - MSP:ADR-0068
  - MSP:ADR-0077
author: endazon (with Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md
  - planning:projects/microservices-platform/07_adr/ADR-0077_operation-semantics-in-three-level-slice.md
---

# 仕様書: #613 第5弾 —— TradeDecision / OrderExecution / Backtest の 3 段化移送

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（構成是正・保守性の非機能作業）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 起点 ID: `NFR`（無採番。`.claude/rules/traceability.md` 無採番許容ケース 2 ——
  ソースツリーの割り方であり計画の非機能要件表に当たる番号が無い。
  [IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) と同じ判断）
- 関連 ADR: platform `ADR-0065` 決定 2・決定 3／platform `ADR-0068` 決定 1〜5／
  🔴 **platform `ADR-0077`（2026-09-03 Accepted。「操作」は契機の形で決めない）**
- 移送規則の正本: [IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 1〜6 ＋
  §追記 4（本作業で足す）。**本 PR で新規 IADR は作らない**
- 先行弾: 第 1 弾 [20260903_613_vsa-three-tier-risk-management](20260903_613_vsa-three-tier-risk-management.md)（規則の確定・`RiskManagementService`）／
  第 2 弾 [20260903_613_vsa-three-tier-report](20260903_613_vsa-three-tier-report.md)（`ReportService`）／
  第 3 弾 [20260903_613_vsa-three-tier-audit-config-cost](20260903_613_vsa-three-tier-audit-config-cost.md)／
  第 4 弾 [20260903_613_vsa-three-tier-market-monitor](20260903_613_vsa-three-tier-market-monitor.md)
- 計画書リンク: <https://github.com/endazon/project-planning/blob/main/projects/microservices-platform/07_adr/ADR-0077_operation-semantics-in-three-level-slice.md>

## 目的・背景

第 1 弾の [IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) §追記 1 は、
**暫定解釈 B（操作＝登録表に登録された HTTP 端点に限る）**を採り、
「HTTP 端点を持たない 5 サービス（Backtest / InformationCollection / Notification / OrderExecution /
TradeDecision）は移送対象なし」と結論していた。同追記は「計画側がこれと異なる裁定を出す場合は追随する」と
自ら述べており、**計画側は 2026-09-03 に `MSP/ADR-0077` で案 C を採って B を退けた**。

`MSP/ADR-0077` の拘束点:

1. **「操作」とは、そのサービスが外部からの 1 つの契機に応えて行う 1 つのユースケースである。**
   🔴 契機の形（HTTP 要求・イベント購読・スケジュール実行・チャットコマンド）では決めない。
   **契機が 2 つある操作は 1 つの操作である**（基盤の `DataSourceService/Features/DataSources/Sync/` が実例）。
2. **分界は「入口の配線」と「操作の処理」。** 入口の配線は現在の置き場に残す
   （HTTP 登録表は 2 段目、イベント購読の宣言は `Infrastructure/Messaging`〔AST では `Infrastructure/Steps/`〕、
   常駐ジョブの起動と間隔設定は常駐ジョブの置き場〔AST では `Hosted/`〕）。**操作の処理は
   `Features/<集約>/<操作>/` へ下ろす。** 判定は `ADR-0068` 決定 2 のまま。
3. 暫定解釈 B は採らない。
4. `ADR-0065` 決定 2 の「実装 38 集約はすべて移送対象」を維持する。

本作業（第 5 弾）は、この裁定を受けて **HTTP 端点を持たない 3 サービス** へ規則を当てる。

実測（移送前・`develop` `7dfc97b5`）:

| サービス | `Features/<集約>/` の `.cs` | 操作ディレクトリ | HTTP 端点 | 購読ハンドラ | 常駐ジョブ | `Tests/` の `.cs` | テスト件数 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `TradeDecisionService` | 22 | 0 | 0 | 2 | 0 | 46 | 459 |
| `OrderExecutionService` | 19 | 0 | 0 | 2 | 6 | 34 | 302 |
| `BacktestService` | 5 | 0 | 0 | 0 | 0 | 34 | 241 |

全アセンブリ合計 **5444 件**（移送前）。

## 対象範囲

- 対象:
  - 3 サービスの `Features/<集約>/<操作>/` 3 段化
  - 3 サービスの `Tests/` を本体の樹形の鏡写しへ再配置（[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 5）
  - [IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) への日付付き追記（§追記 4）——
    §追記 1（案 B）の撤回と、`MSP/ADR-0077` の分界を AST の置き場へ写す規則
- 対象外:
  - `InformationCollectionService` / `NotificationService`（**第 6 弾**）
  - 集約（2 段目）の切り直し・`Domain/` 欠けの是正（別 PR）
  - 常駐ジョブ・購読ハンドラの置き場の変更（`MSP/ADR-0077` §結果「購読ハンドラ・常駐ジョブの置き場は
    動かない。移すのは `Features/` に既に居るファイルだけである」に従う）

## 移送規則（本弾で当てる形）

[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 1〜6 に、`MSP/ADR-0077` を受けた
**§追記 4** を足す。要点（正本は IADR 本文）:

1. **操作 ＝ 1 ユースケース。契機の形では決めない。** AST の契機は
   購読（`Infrastructure/Steps/*Handler.cs`）・常駐（`Hosted/*Service.cs`）・HTTP の 3 形。
   **同じユースケースを 2 つの契機が駆動する場合は 1 操作**（`TradeDecisionService` が実例）。
2. **入口の配線は動かさない。** 購読ハンドラ型は `Infrastructure/Steps/` に残し**名前空間も変えない**
   （Wolverine のハンドラ探索・キュー名・`check-consumer-endpoint-names` 系の検査に効く）。
   `Hosted/` の常駐型も残す。
3. **移すのは `Features/<集約>/` に既に居るファイルだけである**（`MSP/ADR-0077` §結果の明文）。
   `Hosted/` や `Infrastructure/Steps/` の中に**インラインで書かれた 1 巡回の処理を新規ファイルへ
   切り出すことはしない** —— それは移送ではなく設計変更であり、DI 登録の新設を伴う。
4. **参照元の数え方**（[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 2 の延長）:
   - 数えない: `Program.cs`（DI 登録）・`Tests/`・**コメント内の言及**（実測で除外する）
   - **1 操作の入口として数える**: `Infrastructure/Steps/<X>Handler.cs`・`Hosted/<Y>Service.cs`
   - **2 段目へ固定する**: `Infrastructure/ExternalServices/`・`Infrastructure/Persistence/`（ポートの実装）から
     使われるもの、他サービスから使われるもの、2 つ以上の操作から使われるもの

## 操作の割り当て（実測にもとづく確定）

### `TradeDecisionService` / `Features/TradeDecision/`

**操作は 1 つである。** 2 つの購読ハンドラはいずれも「1 銘柄について AI 取引判断を下し、成立したら
発注意図（`TradeDecisionMade`）を発行する」という同一のユースケースを駆動しており、
`MSP/ADR-0077` 決定 1 の「契機が 2 つある操作は 1 つの操作である」に当たる。

| 操作 | 契機 | 3 段目へ下ろすファイル |
| --- | --- | --- |
| `DecideTrade` | ①購読 `InformationCollected`（`Infrastructure/Steps/InformationCollectedHandler.cs`＝定時系統）<br>②購読 `PriceMovementDetected`（`Infrastructure/Steps/PriceMovementDetectedHandler.cs`＝価格変動系統） | `TradeDecisionAppService.cs`・`DecisionOrchestrator.cs`・`TradeDecisionPromptBuilder.cs`・`ScreeningContextAssembler.cs` |

2 段目に残る 18 ファイルと根拠:

| ファイル | 残す根拠 |
| --- | --- |
| `DecisionOrchestrationOptions.cs` | `Infrastructure/ExternalServices/DecisionOptionsLoader.cs` |
| `ProfitabilityGateOptions.cs` | `Infrastructure/ExternalServices/ProfitabilityGateOptionsLoader.cs` |
| `DecisionTrigger.cs` | `Infrastructure/ExternalServices/`（`KnowledgeBaseRetrievalContextProvider` ほか 3 本）＋`Infrastructure/Steps/` |
| `RetrievalSourcePolicy.cs` | 他アセンブリ（`InformationCollectionService/Domain/DegradationNotice.cs`・`AiStockTrading.Architecture.Tests`） |
| `ICurrentPriceProvider.cs` `IDailyPolicyProvider.cs` `IDailyPolicyUnconfirmedNotifier.cs` `IFxRateProvider.cs` `IHeldPositionProvider.cs` `ILlmCompletionClient.cs` `ILlmGovernanceReporter.cs` `ILlmUsageReporter.cs` `IMarketCalendar.cs` `IProfitabilityAssumptionsProvider.cs` `IRetrievalContextProvider.cs` `IScreeningReductionReporter.cs` `ISizingContextProvider.cs` `IWatchlistProvider.cs` | ポート。`Infrastructure/ExternalServices/` の実装（および `IMarketCalendar` / `IWatchlistProvider` は `Infrastructure/Steps/`）から使われる |

### `OrderExecutionService` / `Features/OrderExecution/`

| 操作 | 契機 | 3 段目へ下ろすファイル |
| --- | --- | --- |
| `DispatchApprovedOrder` | 購読 `OrderApproved`（`Infrastructure/Steps/OrderApprovedHandler.cs`） | `OrderExecutionAppService.cs`・`OrderDispatchResult.cs`・`OrderDispatchReservationConflictException.cs` |
| `AmendOrder` | `Infrastructure/Steps/OrderAmendmentDispatcher.cs`（訂正・取消の配管。駆動元は #141 / #152） | `OrderAmendmentService.cs` |
| `PollOrderFills` | 常駐 `Hosted/OrderFillPollingService.cs` | `OrderFillPoller.cs`・`FillPollingOptions.cs` |
| `ReconcileOrderReservations` | 常駐 `Hosted/OrderReservationReconciliationService.cs` | `OrderReservationReconciler.cs`・`ReconciliationOptions.cs`・`ReconciliationPolicy.cs` |
| `GuardProtectiveStops` | 常駐 `Hosted/ProtectiveStopGuardService.cs` | `ProtectiveStopGuard.cs`・`ProtectiveStopGuardOptions.cs` |
| `ObserveBrokerAvailability` | 常駐 `Hosted/BrokerAvailabilityProbeService.cs` | `BrokerAvailabilityProbeOptions.cs` |
| `ObserveBrokerPositions` | 常駐 `Hosted/BrokerPositionSnapshotService.cs` | `PositionReconciliationOptions.cs` |

🔴 **8 つ目の契機 `Hosted/OrderReservationRetentionService.cs`（終端予約のパージ）には操作フォルダを作らない。**
1 巡回の処理（`PurgeOnceAsync`）が常駐型の中にインラインで書かれており、`Features/` に該当ファイルが
1 つも無いためである（§移送規則 3）。切り出しは移送ではない。

2 段目に残る 6 ファイル（すべてポート。`Infrastructure/Persistence/` ないし `Infrastructure/ExternalServices/`
の実装から使われる）: `IClientOrderIdBroker.cs`・`IExecutedOrderStore.cs`・`IOrderLifecycleStore.cs`・
`IOrderReservationStore.cs`・`IProtectiveStopOrderStore.cs`・`IReservationBrokerProbe.cs`

> **`BrokerAvailabilityProbeOptions.cs` は第 1 弾の割り当て表で「`RiskManagementService` からも参照される」と
> されていたが、実測では 3 件すべてコメント内の言及**（クランプ上限の同値性の説明）**であり、
> 実参照は `Hosted/BrokerAvailabilityProbeService.cs` と `Program.cs` だけである。** よって 3 段目へ下ろす。

### `BacktestService` / `Features/Backtest/`

🔴 **本サービスは実行時の契機を 1 つも持たない**（HTTP 0・購読 0・常駐 0）。本番戦略（`IBacktestStrategy` 実装）が
未実装で、定時トリガと `BacktestEvaluated` の実 publish は #82 系に残っている（`Program.cs` 冒頭のコメントが自認）。
**`MSP/ADR-0077` 決定 1 は操作をユースケースで定義するため、契機の結線待ちであることは操作の不在を意味しない。**
計画のユースケース（FR-15 バックテスト実行／FR-20 Stage 0 判定）で切る。

| 操作 | 契機 | 3 段目へ下ろすファイル |
| --- | --- | --- |
| `RunBacktest` | 🔴 未結線（#82 で定時トリガを載せる。載せる先は `Hosted/` であり本操作フォルダではない） | `BacktestRunner.cs` |
| `EvaluateStage0Gate` | 🔴 未結線（同上。verdict の実 publish も #82） | `Stage0GateService.cs`・`BacktestEvaluatedFactory.cs` |

2 段目に残る 2 ファイル: `IBarDataSource.cs`（`Infrastructure/ExternalServices/` の 2 実装）・
`IHistoricalBarSource.cs`（`Infrastructure/ExternalServices/` の 5 実装＋`Program.cs`）

## `Tests/` の鏡写し（[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 5）

3 サービスとも `Tests/` は現在ほぼフラットである（`BacktestService/Tests/Calibration/` のみ既存の
サブディレクトリで、**移送前からの形として尊重し動かさない**）。本体の樹形へ写す:

- `Tests/Features/<集約>/<操作>/` —— 操作の処理のテスト
- `Tests/Domain/` —— 純ドメインのテスト
- `Tests/Infrastructure/Steps/` —— 購読ハンドラのテスト
- `Tests/Infrastructure/ExternalServices/` ／ `Tests/Infrastructure/Persistence/` —— アダプタのテスト
- `Tests/Hosted/` —— 常駐ジョブのテスト
- `Tests/` 直下 —— `Program.cs` に対応する配線テスト（`*WiringTests` / `*SelectionTests` /
  `*RegistrationTests` / `HealthEndpointTests`）とテスト土台（フィクスチャ・テストダブル）

**テストの名前空間は `<Svc>.Tests` のまま据え置く**（決定 5）。

## 受け入れ基準

- [ ] `dotnet build backend/backend.slnx` が**警告 0** で通る
- [ ] `dotnet test backend/backend.slnx --filter "Category!=Integration"` の件数が移送前と**同数**
      （全体 5444／TradeDecision 459／OrderExecution 302／Backtest 241）
- [ ] 3 サービスそれぞれの `Features/<集約>/` に操作ディレクトリが 1 つ以上ある
      （TradeDecision 1・OrderExecution 7・Backtest 2）
- [ ] `AiStockTrading.Architecture.Tests` が緑（`DomainSourceDependencyTests` / `RetrievalSourceVocabularyTests` 含む）
- [ ] `dotnet format backend/backend.slnx --verify-no-changes` が通る
- [ ] `node scripts/check-trace-blocks.js` / `check-test-traceability.js` / `check-adr-index-sync.js` /
      `check-doc-links.js` / consumer・queue 名の検査器が通る
- [ ] 公開面（HTTP・wire 契約・DI 登録の意味・Wolverine のハンドラ探索）に差分が無い
      —— 購読ハンドラ型と `Hosted/` 型は名前空間ごと不動である

## 計画書との差異

- 差異: なし。`MSP/ADR-0077` 決定 1〜4 の形をそのまま採る。
  `ADR-0065` の樹形に無い `Hosted/` の扱いは
  [IADR-0276](../adr/IADR-0276_claude-md-vsa-correction-and-hosted-placement.md) 決定 2（現状維持）に従う
  （`MSP/ADR-0077` §残るもの も「本 ADR はこの不整合を決めない」としている）。

## 未決事項

- **`Hosted/` がトップレベルにあること**（基盤は常駐ジョブを操作フォルダの中に置く）。
  `MSP/ADR-0077` §残るもの が「別に裁定が要る」として明示的に開いている。本弾では動かさない。
- **`OrderReservationRetentionService` の 1 巡回の処理**（`PurgeOnceAsync`）のように、
  常駐型の中にインラインで書かれた処理をどう扱うか。本弾は「切り出さない」を採った（§移送規則 3）。
  切り出す判断をするなら、DI 登録の新設を伴う設計変更として別 PR で扱う。
- **`BacktestService` の契機の結線**（#82）。結線先は `Hosted/` であり、本弾で作った操作フォルダは
  そのときの受け皿になる。
