---
title: リスク管理 Worker ホスト（MassTransit 消費者・PostgreSQL 永続化・Keycloak 認可エンドポイント）
type: spec
status: review
related_ids: [FR-10, FR-11, FR-17, FR-19, FR-20, UC-01, UC-02, UC-06, ADR-0001, ADR-0003, ADR-0007]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/06_settings-and-emergency-stop.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: リスク管理 Worker ホスト

> Issue [#12](https://github.com/endazon/ai-stock-trading/issues/12) の **Slice B**。Slice A の
> アプリケーション層（[20260709_risk-management-application](20260709_risk-management-application.md)）と、移植した
> ランタイム Foundation（[20260710_foundation-min-port](20260710_foundation-min-port.md)）の上に、実際に稼働する
> Worker ホストを組む。MassTransit RabbitMQ 消費者・PostgreSQL(EF Core) 永続化・Keycloak 認可エンドポイントを実装する。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-10（リスク統制）、FR-11（監査・変更履歴）、FR-17（設定の一元管理・バージョン）、FR-19（取引ガード）、FR-20（段階ゲート）
- ユースケース（UC）: UC-01/UC-02（取引サイクルの判定段）、UC-06（設定変更・緊急停止）
- ADR: ADR-0001（platform 再利用・Database per Service）、ADR-0003（判定は決定的コード・kill switch・損切り機械執行）、ADR-0007（変更は利用者のみ・履歴記録）
- 関連 IADR: [IADR-0010](../adr/IADR-0010_risk-service-layering-and-slicing.md)（層構成・スライス）、[IADR-0011](../adr/IADR-0011_foundation-min-port.md)（Foundation）、本作業で新規 [IADR-0012](../adr/IADR-0012_risk-settings-persistence.md)（設定永続化＝JSONB＋楽観排他）
- 対象 Issue: #12（Slice B）

## 目的・背景

Slice A で `OrderScreeningService` 等の判定・状態管理ロジックをインフラ非依存で実装し、Foundation 移植で共通の
バス再試行・可観測性・認証・ヘルスを用意した。本作業はこれらを配線し、`TradeDecisionMade` を購読して
`OrderApproved`/`OrderRejected` を発行する稼働サービスにする。設定・kill switch・ロックアウト・変更履歴を
PostgreSQL に永続化し（ADR-0001 Database per Service）、kill switch 操作・設定変更を利用者のみの HTTP エンドポイントで
提供する（ADR-0007・Keycloak `OwnerOnly`）。

## 対象範囲

新規プロジェクト `RiskManagementService.Worker`（`AiStockTrading.RiskManagement.Worker`・`Microsoft.NET.Sdk.Web`）と
テスト `RiskManagementService.Worker.Tests`。platform の Worker パターン（ConversionService.Worker）に倣う。

### ホスト配線（`Program.cs`）

Serilog（`ConfigureAiStockTradingSerilog`）／`AddAiStockTradingObservability`／`AddAiStockTradingAuth`／
`AddDbContext<RiskManagementDbContext>`（Npgsql）／`AddAiStockTradingHealthChecks().AddNpgSql(tags:["ready"])`／
`AddMassTransit`（RabbitMQ・`TradeDecisionMadeConsumer`・`UseAiStockTradingRetry`）／起動時 `MigrateAsync`（relational時）／
`MapAiStockTradingHealthChecks`／`UseAiStockTradingMiddleware`／`MapRiskControlEndpoints`。

### MassTransit 消費者（`Composable/Steps/TradeDecisionMadeConsumer.cs`）

`IConsumer<TradeDecisionMade>`。DI スコープで `OrderScreeningService` を解決し `Screen(decision)` を実行、
結果に応じ `OrderApproved` or `OrderRejected` を `context.Publish` する。

### 永続化（`Foundation/Persistence/`・EF Core）

`RiskManagementDbContext` と、Application 層ポートの EF 実装:
- `EfRiskSettingsStore`（`IRiskSettingsStore`）: 設定は単一行 `RiskSettingsRow`（`RiskManagementSettings` を JSON 直列化）＋
  `Version` 列で**楽観的排他制御**（Slice A レビュー指摘・[IADR-0012](../adr/IADR-0012_risk-settings-persistence.md)）。
- `EfKillSwitchStore`（`IKillSwitchStore`）: 単一行 `KillSwitchRow`。
- `EfLockoutStore`（`ILockoutStore`）: 単一行 `LockoutRow`（不在=ロックなし）。
- `EfSettingsChangeLog`（`ISettingsChangeLog`）: 追記専用 `SettingsChangeRow`（FR-11）。
- EF マイグレーション `InitialCreate`（Postgres）。専有スキーマ/DB（`risk_management_svc`・ADR-0001）。

### ポート実体（`Composable/Adapters/`）

`IPortfolioStateProvider` の実装は保有・約定・損益の実データ（#13/#17）に依存する。本 PR では**プレースホルダ実装**
`PlaceholderPortfolioStateProvider`（`Capital=TradingDefaults.InitialCapital`・その他ゼロ）を置き、#13/#17 連携で
差し替える旨をコメント・未決事項に明記する。

### 認可エンドポイント（`Foundation/Endpoints/RiskControlEndpoints.cs`）

`/risk-controls` グループ、すべて `RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly)`（利用者のみ・ADR-0007）:
- `POST /kill-switch/engage`・`POST /kill-switch/disengage`（body: reason）→ `KillSwitchService`。actor はトークンの名前。
- `GET  /kill-switch` → 現在状態。
- `PUT  /settings/limits`・`/settings/guard`・`/settings/stage` → `RiskSettingsService`。
- `GET  /settings` → 現行設定。`GET /settings/history` → 変更履歴（FR-11）。

## 受け入れ基準

CI で緑にする範囲（ユニット＋WebApplicationFactory・InMemory DB・TestAuthHandler）:
- [ ] `TradeDecisionMadeConsumer` が承認時 `OrderApproved`・拒否時 `OrderRejected` を発行する（MassTransit テストハーネス）。
- [ ] kill switch/設定エンドポイントは `OwnerOnly`（`trading-owner`）必須で、未認証 401・ロール無し 403。
- [ ] kill switch engage/disengage が永続化され、`GET /kill-switch` に反映される。設定変更が履歴に残る（FR-11）。
- [ ] EF ストアが InMemory DB で設定ラウンドトリップ・楽観排他（version 不一致で失敗）・履歴追記を満たす。
- [ ] `GET /health/live`・`/health/ready` が応答する。
- [ ] 既存テスト（計 101）を緑に保つ。

実コンテナ前提（CI 既定では実行しない・devcontainer/Testcontainers）:
- [ ] RabbitMQ 経由の `TradeDecisionMade`→発行の E2E。
- [ ] Postgres への `MigrateAsync` とスキーマ検証。

## 対象外（後続）

- 損切りの機械執行（Slice C・#10 依存）。
- `IPortfolioStateProvider` の実データ供給（#13/#17）。
- #22 本体（イベント共通エンベロープ・宣言的バインディング・自己申告）。

## テスト方針

- 消費者は `MassTransit` の `ITestHarness`（インメモリ）で発行を検証。
- エンドポイントは `WebApplicationFactory<Program>` ＋ `TestAuthHandler`（`X-Test-Roles`）＋ InMemory DbContext で検証
  （platform AuthorizationService.Api.Tests 準拠）。
- Testcontainers ベースの統合テストは本 PR では追加せず、CI 非依存の後続作業とする（未決事項）。

## 関連仕様

- 先行: [20260709_risk-management-application](20260709_risk-management-application.md)（Slice A）、[20260710_foundation-min-port](20260710_foundation-min-port.md)（Foundation）
- データ仕様: [risk-management-aggregates](../data/risk-management-aggregates.md)（永続化方針）
- 実装ADR: [IADR-0012](../adr/IADR-0012_risk-settings-persistence.md)

## 未決事項

- Keycloak レルム/ロール（`trading-owner`）の実値は運用構成で確定。
- `IPortfolioStateProvider` の実データ供給は #13（約定）/#17（損益・監査）連携で確定。
- Testcontainers（Postgres/RabbitMQ）ベースの統合テストは CI ゲート整備（#24）と併せて後続で追加。
