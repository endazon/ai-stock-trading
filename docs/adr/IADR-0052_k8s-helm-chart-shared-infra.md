---
title: IADR-0052 AST の k8s デプロイは Helm chart（10 Worker 同型テンプレート）とし、共有インフラは MSP platform-infra を ExternalName で参照する
type: impl-adr
status: Accepted
related_ids:
  - ADR-0006 # インフラ・デプロイ（k3s）
  - ADR-0001 # platform 再利用 / DB per service
  - IADR-0048 # ユニット実行環境（compose）
author: claude
created: 2026-07-13
updated: 2026-07-13
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md"
---

# IADR-0052: AST の k8s デプロイは Helm chart とし、共有インフラは MSP platform-infra を参照する

- 状態: Accepted
- 日付: 2026-07-13
- 決定者: claude（実装）

## 起点・関連

- 関連計画 ID: ADR-0006（Hetzner k3s・GitOps）／ADR-0001（platform 再利用・DB per service）
- Issue: #122（AST k8s chart）／ #24（k3s デプロイ）／ #121（取引サイクル CronJob）
- 対（MSP 側）: microservices-platform IADR-0066（ローカル k8s dev・platform-infra・ExternalName 方式）

## コンテキストと課題

AST には k8s デプロイ資産が無く（`backend/Dockerfile` ＋ compose のみ・#24）、MSP と連結した
ローカル k8s(k3d) dev 環境で 10 Worker を稼働させる必要がある（#121 の CronJob 検証の前提）。
10 Worker は全て Web SDK（:8080・`/health/{live,ready}`）で同型であり、接続先インフラ
（Postgres/RabbitMQ/Keycloak/otel）は MSP 側と共有する。

## 決定

1. **Helm chart** `deploy/helm/ai-stock-trading` を新設し、10 Worker を**同型の Deployment/Service
   テンプレート**（`range .Values.services`）でレンダリングする。#24（GitOps）へ再利用できる形にする。
2. **共有インフラは MSP の `platform-infra` を参照**する。AST namespace 内に ExternalName エイリアス
   （`postgres`/`rabbitmq`/`keycloak`/`otel-collector` → `*.platform-infra.svc`）を張り、appsettings/compose
   と同じ**素のサービス名**で解決させる（MSP/IADR-0066 と同一方式）。DB は共有 Postgres 上の AST 用
   ユーザ `ai`＋専有 DB（`*_svc`。MSP infra init が作成）を用いる（ADR-0001: DB per service）。
3. **外部連携は fail-safe 既定**で chart 変数化する。`ast-secrets`（空既定）を明示設定した時のみ有効化:
   `ANTHROPIC_API_KEY`（空=Placeholder LLM・#79）／Finnhub（空=NoOp・#81）／Discord（空=NoOp・#15）／
   `Broker__Provider=paper`（実発注しない・moomoo は simulate 前提・#13）。secretKeyRef は `optional: true`。
4. **#121 の CronJob は骨子のみ・既定 disabled**（`tradingCycle.cronjob.enabled=false`）。有効化時は
   収集の run-once（`/internal/collection/run-once`）を叩く。**未実装のため既定無効**とし、在来の
   in-process ポーリング（IADR-0023）を fail-safe で維持する。run-once の C# 実装は #121 の後続で行う。

## 根拠・トレードオフ

- 同型テンプレートで 10 Worker の重複を排し、追加/変更を values 集約する（保守容易）。
- ExternalName 方式は chart を無改修に保ち、MSP/AST を疎結合にする（インフラの所有は MSP 側）。
- CronJob を既定無効にすることで、トリガ実装前でも安全（在来動作を壊さない）に chart を導入できる。

## 影響

- 追加: `deploy/helm/ai-stock-trading/**`、`scripts/k8s-local-{images,deploy}.sh`、作業仕様書。
- 変更なし: `backend/**`（コード）・`docker-compose.yml`・`infra/**`。
- 後続: #121（run-once エンドポイント＋スケジューラモード切替・TDD）で CronJob を実効化する。

## 代替案

- **各 Worker 個別マニフェスト**: 重複が大きい。→ 不採用（同型テンプレート）。
- **AST 側で infra を再デプロイ**: 二重運用・リソース増。→ 不採用（platform-infra 共有）。
- **CronJob を既定有効**: run-once 未実装で毎回失敗する。→ 不採用（既定無効・fail-safe）。
