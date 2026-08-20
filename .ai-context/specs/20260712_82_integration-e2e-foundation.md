---
title: 実コンテナ/実 API 統合 E2E 基盤（Testcontainers・compose healthcheck・CI 分離）
type: spec
status: review
related_ids:
  - IADR-0049
  - IADR-0048
  - IADR-0013
author: claude
created: 2026-07-12
updated: 2026-07-12
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0003_messaging-masstransit-rabbitmq.md
---

# 仕様書: 実コンテナ/実 API 統合 E2E 基盤（issue #82・Slice A）

> #107（compose スタック / IADR-0048）を土台に、実 RabbitMQ・実 PostgreSQL を起動しての統合 E2E を
> 成立させる。既定 CI（ユニット・純ロジック）とは分離する。設計判断は
> [IADR-0049](../adr/IADR-0049_integration-e2e-foundation.md)。

## 起点となる計画書（トレーサビリティ）

- 起点: issue #82（実コンテナ/実 API E2E 基盤・#107 の前提の上に構築）
- 参照: platform ADR-0003（MassTransit/RabbitMQ）・IADR-0013（standalone 配線は test-only）・
  IADR-0048（compose/appsettings）
- 依存: #76（s2s 認証・同期照会の実トークン伝播＝Slice B/C・本 PR では扱わない）
- fail-safe 分離: #13（実発注）・#81（実市場データ）・#79（実 LLM 費用）は対象外・既定 no-op

## 目的・背景

standalone 配線（IADR-0013）は実基盤で未検証。#22 系は実 RabbitMQ/PostgreSQL/Keycloak・実 HTTP・s2s
認証を伴う E2E を一貫して先送りしてきた。本作業でその自動 E2E 基盤を用意し、既定 CI を不安定化させずに
実基盤結線を検証する。

## 対象範囲（Slice A）

- 対象:
  1. **compose healthcheck**: 各 AST サービス（:8080・`/health/ready`）＋ Keycloak（:9000）＋
     otel-collector（:13133）に healthcheck を追加し、依存を `service_healthy` 前提に整える。
     runtime イメージへ dev 目的の軽量プローブ（`curl`）を追加。
  2. **Testcontainers 統合テスト基盤**: 新規プロジェクト
     `backend/Tests/AiStockTrading.IntegrationTests`。実 PostgreSQL・実 RabbitMQ を Testcontainers で
     起動し、発注執行 Worker を `WebApplicationFactory<Program>` で実基盤へ結線。
  3. **発注執行パイプライン E2E**（ペーパー）: `OrderApproved` を実 RabbitMQ へ発行 → 購読 →
     ペーパー執行 → 実 Postgres の `executed_orders` へ永続 → `OrderExecuted` 発行、を検証。
  4. **CI 分離**: `[Trait("Category","Integration")]` を付し、既定 CI は `--filter Category!=Integration`
     で除外、専用 `integration.yml`（schedule + workflow_dispatch）で `Category=Integration` のみ実行。
- 対象外（Slice B/C・#82 内で追跡）:
  - Keycloak 実トークン（`trading-owner`）による OwnerOnly 同期照会（daily-policy / sizing-context /
    open-positions / costs-state）の E2E。
  - 複数サービス通しのパイプライン E2E・s2s トークン伝播（#76）。

## 設計方針（要点。詳細は IADR-0049）

1. 自動 E2E は Testcontainers（必要な実基盤のみ・in-process Worker）を主経路とし、compose は full/manual
   E2E の経路として healthcheck 込みで維持する。
2. 既定 CI から `Category=Integration` を除外（ビルドはする・実行しない）。実基盤 E2E は nightly/dispatch。
3. 発注は既定ペーパー（実弾を撃たない・IADR-0016）。実発注・実市場・実 LLM は対象外。
4. サービス横断の `Program` 曖昧回避のため、Slice A の in-process 対象 Worker は発注執行 1 つに限定。

## 受け入れ基準（issue #82 の受け入れ条件へ写像）

- [x] docker-compose/testcontainers で実 RabbitMQ・PostgreSQL を起動する統合テスト基盤を用意（Slice A）
- [x] 主要パイプラインの E2E をペーパーモードで検証（Slice A: 発注執行パイプライン＝OrderApproved→執行→永続→OrderExecuted）
  - 情報収集→…→発注、損切り検知→機械執行の通し E2E は Slice B/C で拡張（本 PR で基盤を用意）
- [ ] サービス間同期照会（daily-policy/sizing-context/open-positions/costs-state）を実 HTTP＋認証（#76）付きで検証 → **Slice B/C（#76 依存）へ分離**（本 PR は基盤と healthcheck まで）
- [x] CI 上は別ジョブ/別トリガー（nightly 等）としてユニット CI と分離（Slice A）
- [x] 各サービスに healthcheck を追加し、compose の依存/起動順を healthy 前提に整える（Slice A）

## 検証方法

- ローカル（Docker 実行）: `dotnet test --filter Category=Integration` で発注執行 E2E が緑（実 PG＋実 MQ 起動）。
- 既定 CI: `--filter Category!=Integration` でユニットのみ緑・実行時間/安定性に影響しないこと。
- compose: `docker compose config` で healthcheck/依存の構文検証。可能なら infra + 発注執行の起動 healthy を確認。

## 計画書との差異

- 差異なし（IADR-0013 の standalone 配線を実基盤で検証。実接続の実装は各 issue に分離のまま）。

## 未決事項・ユーザー作業

- Slice B/C（OwnerOnly 同期照会 E2E・通しパイプライン・s2s トークン伝播）は #76 の進捗に依存。本 PR 完了後に
  #82 内で継続する。
- 機密は不要（E2E は dev ダミー資格情報・ペーパーモードのみ）。実 LLM/市場/発注を伴う E2E は対象外。
