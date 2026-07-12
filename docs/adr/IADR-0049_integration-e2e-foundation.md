---
title: IADR-0049 実コンテナ統合 E2E は Testcontainers を基盤とし、CI から分離する
type: impl-adr
status: Accepted
related_ids:
  - IADR-0013 # PlatformShim は test-only / 本番非使用
  - IADR-0048 # 実行環境スキャフォールド（compose/appsettings/.env.example）
author: claude
created: 2026-07-12
updated: 2026-07-12
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0003_messaging-masstransit-rabbitmq.md"
---

# IADR-0049: 実コンテナ統合 E2E は Testcontainers を基盤とし、CI から分離する

- 状態: Accepted
- 日付: 2026-07-12
- 決定者: endazon（起票: issue #82）・claude（実装詳細）

## 起点・関連

- 起点 issue: #82（実コンテナ/実 API 統合 E2E 基盤・#107 の compose を土台）
- 前提: [IADR-0048](IADR-0048_runtime-scaffold.md)（compose/appsettings/.env.example）が develop マージ済み
- 制約: [IADR-0013](IADR-0013_platform-foundation-testsupport-shim.md)（standalone 配線・PlatformShim は本番非使用）
- 関連 issue: #76（service-to-service 認証・同期照会の実トークン伝播）・#22（サービス間連携）
- 関連する作業仕様書: [作業仕様書](../specs/20260712_82_integration-e2e-foundation.md)

## コンテキストと課題

`#22` 系の各 PR は、CI を **ユニット＋fake HttpMessageHandler＋WebApplicationFactory（InMemory EF・
TestAuth・MassTransit テストハーネス）** に限定し、**実 RabbitMQ/PostgreSQL/Keycloak・実 HTTP・s2s 認証を
伴う E2E は一貫して先送り**してきた。standalone 配線（IADR-0013）は実基盤で未検証である。#82 はこの
E2E を成立させ、かつ **既定 CI を不安定化させない**ことを求める。

論点は次の 3 点。

1. **実基盤の起動方式**: compose スタック（#107）を丸ごと起動して黒箱 E2E か、テストコードから
   必要な実基盤だけを起動（Testcontainers）して in-process の Worker を実基盤へ結線するか。
2. **CI 分離**: 実基盤依存テストは Docker と時間を要し、pull_request 既定 CI に混ぜると不安定・低速化する。
3. **スコープ（#76 依存）**: 同期照会（daily-policy / sizing-context / open-positions / costs-state）は
   すべて OwnerOnly（Keycloak `trading-owner`）を要求する。呼び出し側のトークン伝播は #76（未完）。

## 決定

### 決定 1: 自動 E2E は Testcontainers を基盤にする（compose は full/manual E2E の経路として維持）

- xUnit の統合テストから **Testcontainers**（`Testcontainers.PostgreSql` / `Testcontainers.RabbitMq` ほか）で
  実基盤コンテナを起動し、対象 Worker を `WebApplicationFactory<Program>` で in-process 起動して
  **実 Npgsql・実 RabbitMQ（MassTransit）へ結線**する。利点:
  - サービスイメージ 10 個をビルドせずに実基盤結線を検証できる（速く hermetic）。
  - 実 EF Migration の適用・実キュー往復・実 DB 永続を Worker の実 DI 配線のまま検証できる（IADR-0013 の
    standalone 配線をそのまま対象化）。
- #107 の compose スタックは、全サービスを起動する **full/manual E2E** の経路として維持し、healthcheck を
  加えて起動順を healthy 前提に整える（決定 3）。

### 決定 2: 既定 CI から分離し、`Category=Integration` トレイトで制御する

- 統合テストは `[Trait("Category","Integration")]` を付す。既定 CI（`ci.yml` の build-and-test）は
  `--filter "Category!=Integration"` で **実行から除外**（ビルドはする）。
- 実基盤 E2E は専用ワークフロー（`integration.yml`・`schedule` ＋ `workflow_dispatch`）で
  `--filter "Category=Integration"` のみ実行する。GitHub の ubuntu ランナーは Docker 同梱のため
  Testcontainers が動作する。既定 CI の安定性・速度を損なわない。

### 決定 3: compose に healthcheck を追加し、依存を healthy 前提にする

- 各 AST サービス（Web SDK・:8080）に `/health/ready`（依存 DB 等の "ready" チェック）ベースの
  healthcheck を付す。runtime イメージに軽量プローブ（`curl`）を dev 目的で導入する。
- Keycloak（`KC_HEALTH_ENABLED`＋管理ポート :9000 の `/health/ready`）・otel-collector（:13133 の
  health_check 拡張）に healthcheck を付し、依存側（Auth サービス・app）は `service_healthy` を待つ。

### 決定 4: スコープをスライスし、#76 依存分を分離する

- 本 PR（**Slice A**）: (1) compose healthcheck 完備、(2) Testcontainers 基盤＋**発注執行パイプラインの
  実基盤 E2E**（`OrderApproved` → 実 RabbitMQ 購読 → ペーパー執行 → 実 Postgres 永続 → `OrderExecuted` 発行）、
  (3) CI 分離（nightly ジョブ＋既定 CI 除外フィルタ）。
- 後続（**Slice B/C**・#82 内で追跡）: Keycloak 実トークン（`trading-owner`・dev realm ROPC）による
  OwnerOnly 同期照会エンドポイント E2E、複数サービス通しのパイプライン E2E、s2s トークン伝播（**#76**）。
- fail-safe を維持: 実発注（#13）・実市場データ（#81）・実 LLM 費用（#79）は本 issue 対象外・既定 no-op。
  E2E はペーパーモード（実弾を撃たない・IADR-0016）で行う。

## 影響

- 追加する統合テストプロジェクトは `backend/backend.slnx` に含めるが、既定 CI では `--filter` で実行除外
  するため、build-and-test の実行時間・安定性への影響はビルド分のみ。
- Testcontainers は Docker を要求する。ローカル/nightly でのみ実走し、pull_request 既定 CI では走らせない。
- compose の healthcheck・runtime イメージへの `curl` 追加は dev/E2E 目的。本番配備は platform（#24）の管掌。

## 却下した代替案

- **compose 丸ごと起動の黒箱 E2E を CI 主経路にする**: 10 イメージのビルドと全サービス起動が重く、
  nightly でも遅い。Testcontainers（必要な実基盤のみ・in-process Worker）を主経路にし、compose は
  manual full E2E として残す（決定 1）。
- **統合テストを既定 CI で実行**: Docker 依存で不安定・低速化し、issue の「ユニット CI と分離」に反する。
  トレイト分離＋専用ワークフローにする（決定 2）。
- **サービスごとに Program を横断参照する単一統合プロジェクト**: 各 Worker が global 名前空間に
  `public partial class Program` を宣言するため多重参照は `Program` が曖昧になる。Slice A は対象 Worker を
  1 つ（発注執行）に絞り、以降のサービスは別スライス/別プロジェクトで扱う。
