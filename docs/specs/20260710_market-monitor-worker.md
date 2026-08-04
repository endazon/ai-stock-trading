---
title: 市場監視 Worker ホスト（ポーリングループ・市場開場判定・MassTransit 発行・基準値更新・永続化）
type: spec
status: review
related_ids: [FR-03, FR-13, FR-17, UC-02, ADR-0001, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/04_workflows/02_event-driven-trading.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: 市場監視 Worker ホスト

> Issue [#10](https://github.com/endazon/ai-stock-trading/issues/10) の **Slice B**。Slice A の判定コア／アプリ層
> （[20260710_market-monitor-core](20260710_market-monitor-core.md)）と test-support shim（IADR-0013）の上に、稼働する
> Worker ホストを組む。ポーリングループ・市場開場判定・MassTransit 発行・`TradeDecisionMade` 購読による基準値更新・
> PostgreSQL 永続化を実装する。実行時基盤（Serilog/OTel/MassTransit/認証）は本番非使用 shim を用いる（IADR-0013）。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-03（価格変動監視・即時起動）、FR-13（監視対象・閾値の設定可能）、FR-17（設定の一元管理）
- ユースケース（UC）: UC-02（価格変動トリガー取引）
- 業務フロー: `04_workflows/02_event-driven-trading.md`（監視間隔ごとのポーリング・市場閉場中は停止・損切り優先）
- ADR: ADR-0001（platform 再利用・Database per Service）、ADR-0003（損切りは機械執行・AI 迂回）
- 関連 IADR: [IADR-0014](../adr/IADR-0014_market-monitor-events-and-boundary.md)（イベント・責務境界）、[IADR-0013](../adr/IADR-0013_platform-foundation-testsupport-shim.md)（shim）、[IADR-0012](../adr/IADR-0012_risk-settings-persistence.md)（設定永続化の JSON+Version 方式を踏襲）
- 対象 Issue: #10（Slice B）

## 目的・背景

Slice A で判定ロジックとイベント契約を確定した。本 Slice はそれを配線し、監視間隔ごとに価格をポーリングして
`PriceMovementDetected` / `StopLossTriggered` を MassTransit で発行する稼働サービスにする。基準値（前回判断時点価格）は
`TradeDecisionMade` を購読して更新する。設定・基準値・クールダウンを PostgreSQL に永続化し（ADR-0001）、監視設定は
利用者のみの HTTP エンドポイントで変更できる（FR-13・Keycloak `OwnerOnly`）。

## 対象範囲

新規 `MarketMonitorService.Worker`（`AiStockTrading.MarketMonitor.Worker`・`Microsoft.NET.Sdk.Web`）とテスト。
platform の Worker パターン＋リスク管理 Worker（#12 Slice B）に倣う。

### ホスト配線（`Program.cs`）

Serilog/OTel／`AddAiStockTradingObservability`／`AddAiStockTradingAuth`（shim）／`AddDbContext<MarketMonitorDbContext>`
（Npgsql）／ヘルスチェック(+NpgSql)／`AddMassTransit`（RabbitMQ・`TradeDecisionMadeConsumer`・`UseAiStockTradingRetry`・
`IPublishEndpoint` 発行）／`AddHostedService<MonitorPollingService>`／起動時 `MigrateAsync`／`UseAiStockTradingMiddleware`／
`MapAiStockTradingHealthChecks`／`MapMonitorSettingsEndpoints`。**実行時基盤は shim（本番非使用・IADR-0013）**。

### ポーリング（`Composable/Polling/MonitorPollingService.cs`）

`BackgroundService`。監視間隔（構成 `Monitor:PollIntervalSeconds`・既定 60s）ごとに `RunOnceAsync` を呼ぶ。
`RunOnceAsync`: 市場開場（`IMarketSchedule.IsOpen`）なら `MarketMonitorService.EvaluateRoundAsync` を実行し、
結果の `StopLossTriggered` / `PriceMovementDetected` を `IPublishEndpoint.Publish` する。閉場中はスキップ（監視停止）。
`RunOnceAsync` を単体テスト可能な単位として切り出す（ループの `Task.Delay` から分離）。

### 市場開場判定（`Composable/Adapters/WeekdayMarketSchedule.cs`）

`IMarketSchedule`。本 PR は**平日判定の最小実装**（土日は閉場）。時間帯・祝日を含む正確な市場カレンダーは #21 で差し替える
（ポートで吸収）。

### 基準値更新（`Composable/Steps/TradeDecisionMadeConsumer.cs`）

`IConsumer<TradeDecisionMade>`。判断確定時に対象銘柄の基準値（`IPriceBaselineStore`）を `Intent.Price`（判断時点価格）に
更新する。これにより「変動率＝前回判断時点価格比」が成立する。

### 永続化（`Foundation/Persistence/`・EF Core・IADR-0012 踏襲）

`MarketMonitorDbContext` と EF ストア:
- `EfMonitoredSymbolStore`（`IMonitoredSymbolStore`）: 設定は単一行 JSON＋`Version`（楽観排他）。未設定は `MonitorDefaults`。
- `EfPriceBaselineStore`（`IPriceBaselineStore`）: (Symbol, Market) キーの基準値行。
- `EfCooldownStore`（`ICooldownStore`）: (Symbol, Market) キーの最終トリガー時刻行。
- `InitialCreate` マイグレーション。専有 DB（`market_monitor_svc`・ADR-0001）。

### ポート実体（`Composable/Adapters/`）

`IPositionStore` の実装は保有・損切り価格の実データ（#13/#17）に依存する。本 PR は**プレースホルダ**
`PlaceholderPositionStore`（空・保有なし＝損切り検知対象なし）を置き、初回利用時 1 回警告する（#12 の踏襲）。

### 設定エンドポイント（`Foundation/Endpoints/MonitorSettingsEndpoints.cs`）

`/monitor` グループ、`RequireAuthorization(OwnerOnly)`（利用者のみ・FR-13）:
- `GET /monitor/settings`（現行）／`PUT /monitor/settings`（監視銘柄・閾値・クールダウンの更新）。
検証失敗は 400、楽観排他競合は 409 に写像（#12 Slice B 踏襲）。

## 受け入れ基準

CI で緑にする範囲（ユニット＋WebApplicationFactory・InMemory DB・MassTransit テストハーネス・TestAuthHandler）:
- [ ] `RunOnceAsync` が市場開場時に評価結果を発行する（閾値超過→`PriceMovementDetected`、損切り→`StopLossTriggered`）。
- [ ] 市場閉場（`IMarketSchedule.IsOpen=false`）時は発行しない。
- [ ] `TradeDecisionMadeConsumer` が対象銘柄の基準値を判断時点価格へ更新する。
- [ ] 設定エンドポイントは `OwnerOnly` 必須（未認証 401・ロール無し 403）、変更が永続化・反映される。
- [ ] EF ストアが基準値・クールダウン・設定を InMemory DB でラウンドトリップする。
- [ ] `/health/live` が応答する。既存テスト（計 133）を緑に保つ。

実コンテナ前提（CI 既定では実行しない・Testcontainers）:
- [ ] RabbitMQ 経由の実発行・`TradeDecisionMade` 購読 E2E、Postgres への `MigrateAsync`。

## 対象外（後続）

- **#12 Slice C**: `StopLossTriggered` 購読→決済注文発行（別 issue の別 PR）。
- 実データ供給: `IPositionStore`（保有・損切り価格）は #13/#17。moomoo リアルタイム市況 `IMarketDataSource` 実装は #13。
- 正確な市場カレンダー（時間帯・祝日）は #21。

## テスト方針

- `MonitorPollingService.RunOnceAsync` はハーネスの `IPublishEndpoint` ＋テストダブル（open/closed・評価結果）で検証。
- 消費者は MassTransit `ITestHarness`＋InMemory EF。エンドポイントは `WebApplicationFactory<Program>`＋`TestAuthHandler`。
- Testcontainers ベースの統合テストは本 PR では追加せず、CI 非依存の後続（#24）とする。

## 関連仕様

- 先行: [20260710_market-monitor-core](20260710_market-monitor-core.md)（Slice A）
- 参考: [20260710_risk-management-worker](20260710_risk-management-worker.md)（Worker パターン・永続化・認可の踏襲元）

## 未決事項

- 監視間隔の最適値は moomoo の購読枠・レート制限から逆算（#13 連携で確定）。本 PR は構成既定 60s。
- 市場カレンダー（時間帯・祝日）は #21。損切り価格の算出・保有供給は #13/#17。
