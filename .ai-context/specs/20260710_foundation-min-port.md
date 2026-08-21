---
title: 基盤ランタイム Foundation の最小移植（MassTransit 再試行・可観測性・ヘルスチェック・Keycloak 認証・相関ID）
type: spec
status: review
related_ids: [ADR-0001, NFR, FR-10, FR-11]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# 仕様書: 基盤ランタイム Foundation の最小移植

> Issue [#22](https://github.com/endazon/ai-stock-trading/issues/22)（platform 拡張規約への準拠）の**前段（Slice 1）**。
> 取引ドメインの各サービス（まずはリスク管理 Worker ホスト・#12 Slice B）が platform の慣習に沿って稼働するために
> 必要な**ランタイム Foundation**（MassTransit 共通再試行・可観測性・ヘルスチェック・Keycloak 認証・相関ID）を、
> 基盤実装リポ `../microservices-platform` の `KnowledgePlatform.Shared.Infrastructure/Foundation` から
> `AiStockTrading.Shared.Infrastructure` へ**最小移植**する。#22 本体の 3 要求（イベント共通エンベロープ・宣言的
> バインディング・構成情報 API 自己申告）はこの Foundation の上に載る後続 Slice とする。
>
> **配置・命名の更新（[IADR-0013](../adr/IADR-0013_platform-foundation-testsupport-shim.md)）**: 本移植 Foundation は
> **本番非使用の最小 shim** であり、`src/TestSupport/AiStockTrading.TestSupport.PlatformShim/`（名前空間
> `AiStockTrading.TestSupport.PlatformShim.Foundation.*`）へ物理分離した。本文中の `AiStockTrading.Shared.Infrastructure`
> という配置記述は当初のもので、実配置は TestSupport 側。本番は platform 本体の Foundation を用いる（本番非使用）。

## 起点となる計画書・課題（トレーサビリティ）

- 技術検討 / ADR: ADR-0001（platform 再利用・可変部分への組み込み拡張・基盤無改修・Database per Service）、
  platform ADR-0003（MassTransit 再試行・デッドレター）、platform ADR-0004（Keycloak 認証）、platform ADR-0006（OTel 計装）
- 非機能要件（NFR）: 可観測性・回復性・セキュリティ（認証）
- 関連 IADR: [IADR-0001](../adr/IADR-0001_repo-structure-and-stack.md)（リポ構成・基盤に揃える）、
  本作業で新規: [IADR-0011](../adr/IADR-0011_foundation-min-port.md)（最小移植の範囲と命名方針）
- 対象 Issue: #22（本体）、#12 Slice B（本 Foundation の利用者）

## 目的・背景

`AiStockTrading.Shared.Infrastructure` には現状 `PaperBrokerAdapter` しかなく、platform が全サービス共通で提供する
ランタイム Foundation（バス再試行・OTel 計装・Serilog・ヘルスチェック・JWT 認証・相関ID 伝播）が存在しない。
このままでは #12 Slice B のリスク管理 Worker ホストを platform 慣習に沿って組めない。ADR-0001 は「platform の可変部分へ
組み込む拡張（基盤リポは無改修）」を定めるため、基盤リポのコードを**コピー移植**し、名前空間・命名を `AiStockTrading`
プレフィックスへ揃える（基盤リポは変更しない）。

## 対象範囲

`AiStockTrading.Shared.Infrastructure/Foundation/` に以下を新設する（いずれも基盤リポの対応物の最小移植）。

| 追加物 | 由来（platform） | 提供 API | 用途 |
| --- | --- | --- | --- |
| `Extensions/MassTransitExtensions.cs` | 同名 | `IBusFactoryConfigurator.UseAiStockTradingRetry()` | バス共通再試行（2s/10s/30s→デッドレター。platform ADR-0003） |
| `Extensions/ObservabilityExtensions.cs` | 同名 | `AddAiStockTradingObservability(config, serviceName)` / `ConfigureAiStockTradingSerilog(...)` | OTel トレース/メトリクス・Serilog（OTLP） |
| `Extensions/HealthCheckExtensions.cs` | 同名 | `AddAiStockTradingHealthChecks()` / `MapAiStockTradingHealthChecks()` | `/health/live`・`/health/ready` |
| `Extensions/AuthExtensions.cs` | 同名 | `AddAiStockTradingAuth(config)` ＋ `AiStockTradingAuthPolicies` | Keycloak OIDC/JWT 認証・ロール展開・認可ポリシー |
| `Extensions/KeycloakRolesClaimsTransformation.cs` | 同名 | （内部）`realm_access.roles` → `ClaimTypes.Role` 展開 | RBAC を実 Keycloak トークンで機能させる |
| `Extensions/CommonServiceExtensions.cs` | 同名 | `UseAiStockTradingMiddleware()` | 相関ID・認証・認可のミドルウェア束ね |
| `Middleware/CorrelationIdMiddleware.cs` | 同名 | `X-Correlation-ID` 伝播＋ログ相関 | 分散トレースの相関付け |

### 認可ポリシー（本ドメイン向けの命名）

`AiStockTradingAuthPolicies`:
- `OwnerOnly`（= 利用者のみ）: kill switch 操作・リスク設定変更・段階昇格など「利用者のみ」の操作に用いる
  （FR-10/FR-19/FR-20・ADR-0003 / ADR-0007 / ADR-0008「変更は利用者のみ」）。Keycloak レルムロール `trading-owner` を要求する。

> リスク管理は単独利用者運用（計画書の前提）のため、platform の Admin/Operator 二層ではなく `OwnerOnly` 単層とする。
> ロール名・レルム名は構成（`Auth:Authority`）で差し替え可能。

### パッケージ（`src/Directory.Packages.props` に基盤リポと同一バージョンで追加）

MassTransit 8.4.1 / OpenTelemetry.*（1.16.0・Runtime 1.15.1）/ Serilog.AspNetCore 10.0.0 /
Serilog.Sinks.OpenTelemetry 4.2.0 / Microsoft.AspNetCore.Authentication.JwtBearer 10.0.9。
`AiStockTrading.Shared.Infrastructure.csproj` に `FrameworkReference Microsoft.AspNetCore.App` と上記参照を追加する。

## 対象外（後続 Slice・#22 本体ほか）

- **イベント共通エンベロープ**（#22-①）: `Events/` の record をエンベロープ準拠にする契約テスト。envelope 型は Contracts 側。
- **宣言的バインディング**（#22-②）: pipeline.json・GitOps 適用（platform 側スキーマ確定に依存）。
- **構成情報 API 自己申告**（#22-③）: introspection/drift。段・ポート実装・ガード設定バージョンの実効値申告。
- **オブジェクトストレージ（S3）**: リスク管理では不要のため移植しない。
- リスク管理 Worker ホスト本体（#12 Slice B）: 本 Foundation を利用する別 PR。

## 受け入れ基準

- [ ] `dotnet build` / `dotnet test` が緑（既存 86＋追加テスト）。`dotnet format --verify-no-changes` 準拠。
- [ ] `KeycloakRolesClaimsTransformation` が `realm_access.roles` を `ClaimTypes.Role` へ冪等・fail-closed に展開する（単体テスト）。
- [ ] `CorrelationIdMiddleware` が受領ヘッダを引き継ぎ、無ければ生成してレスポンスへ反映する（単体テスト）。
- [ ] `AddAiStockTradingObservability` / `AddAiStockTradingAuth` が例外なくサービス登録できる（スモークテスト）。
- [ ] 命名・名前空間が `AiStockTrading` プレフィックスで統一され、基盤リポ（`../microservices-platform`）を改修していない。

## テスト方針

- `AiStockTrading.Shared.Infrastructure.Tests`（既存）に Foundation のテストを追加する。ASP.NET 型（HttpContext・
  ClaimsPrincipal）を使うため、テスト csproj に `FrameworkReference Microsoft.AspNetCore.App` を追加する。
- MassTransit の再試行設定・OTLP エクスポータの実挙動は実インフラ依存のため、本 PR ではサービス登録が成立すること
  （スモーク）までを検証し、実配線の E2E は #12 Slice B・統合テスト（devcontainer）で扱う。

## 関連仕様

- 実装ADR: [IADR-0011](../adr/IADR-0011_foundation-min-port.md)（最小移植の範囲・命名）、[IADR-0001](../adr/IADR-0001_repo-structure-and-stack.md)
- 後続: [20260709_risk-management-application](20260709_risk-management-application.md)（#12 Slice A）→ #12 Slice B（本 Foundation を利用）

## 未決事項

- Keycloak レルム名・ロール名（`trading-owner`）の確定は #12 Slice B のエンドポイント実装時に構成で確定する。
- #22 本体（エンベロープ・バインディング・自己申告）は platform 側スキーマ確定に依存するため、確定後に後続 Slice で実装する。
