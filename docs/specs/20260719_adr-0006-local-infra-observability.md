---
title: ADR-0006 インフラ（Vault/可観測性/GitOps）のローカル（経路B）配線 — AST 分
type: work
status: draft
related_ids:
  - ADR-0006
  - NFR
  - IADR-0052
  - IADR-0060
  - IADR-0094
author: endazon (with Claude Code)
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md"
---

# 作業仕様書: ADR-0006 ローカル（経路B）インフラの配線 — AST 分（#24）

> 本作業は **AST リポジトリ内の manifest・docs 追加**に限る。すべて **opt-in / 既定オフ**で、既存の
> 経路B ハーネス（MSP 側 `scripts/k8s-local-up.sh` / `deploy/local/`）は**別 PR**で追加のみ配線する。
> **平文の秘密はコミットしない。** Hetzner 実デプロイ・実 egress・本番相当 NFR は **Tier 3（対象外）**。

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: **[ADR-0006](../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md)**
  （Hetzner・Vault 秘匿・OTel/Prometheus/Loki 可観測性）
- 非機能要件（**NFR**）: 開場時間帯稼働率 99%・認証情報の Vault 秘匿・可観測性
- 関連 IADR: [IADR-0052](../adr/IADR-0052_k8s-helm-chart-shared-infra.md)（AST チャート・共有インフラ）、
  [IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)（決定4＝External Secrets 受け口・Vault 化未充足を明記）、
  本作業の [IADR-0094](../adr/IADR-0094_local-infra-observability-gitops.md)
- 関連 issue: #24（本体）。MSP 側の共有スタック stand-up は別 issue/PR（MSP IADR-0077）。

## 目的・背景

`deploy/helm/ai-stock-trading/` は **External Secrets の受け口**（`external-secrets.yaml`・既定オフ）と
OTLP 送出（`Otlp__Endpoint`→otel-collector）まで既に配線済みだが、values.yaml/README が明記するとおり
**Vault/ESO ストア整備・可観測性バックエンド・GitOps は #24 の管掌で未整備**である。本作業は、そのうち
**AST リポで完結し `helm template` / `kubectl --dry-run` で検証できる分**を追加する。

## 対象範囲

### 実装する（AST PR・すべて opt-in/既定オフ）

1. **GitOps（ArgoCD）骨子**: 新規 `deploy/argocd/`（`appproject.yaml` / `application.yaml` / `README.md`）。
   AST チャート（`deploy/helm/ai-stock-trading`）を `ai-stock-trading` Namespace へ宣言的同期する
   `Application` と、ソース Git・配備先・リソース種別を制約する `AppProject`。ブートストラップのみ kubectl。
2. **秘匿参照の Vault 配線（opt-in）**: `external-secrets.yaml` を拡張し、`externalSecrets.appSecrets.enabled=true`
   のとき **`ast-secrets`（API 鍵群）を Vault から同期する `ExternalSecret`** も描画する。既定オフ＝現行の
   手動 k8s Secret 直運用を維持（fail-safe）。**平文の鍵は values にも manifest にも置かない**（`dataFrom.extract` で
   Vault KV から吸い上げる）。
3. **可観測性（AST 分）**: `deploy/observability/dashboards/ai-stock-trading-overview.json`（Grafana ダッシュボード資産）と
   `docs/observability/observability.md`（OTLP→collector→Prometheus/Loki/Tempo の経路・ローカル stand-up の opt-in 手順）。
4. **docs**: 本作業仕様書・[IADR-0094](../adr/IADR-0094_local-infra-observability-gitops.md)・
   `docs/infra/infra.md`（リポ単位・Tier 境界）・`docs/operations/vault-secrets-runbook.md`（Vault 化 opt-in 手順）。

### 実装しない（別 PR / Tier 3）

- **MSP 側の共有スタック stand-up**（`deploy/local/observability/`・`deploy/local/vault/`・`deploy/local/argocd/`・
  `k8s-local-up.sh` の env ゲート）＝**別 MSP PR**（MSP IADR-0077）。`deploy/keycloak/*realm*.json`（realm-fix）・
  `docker-compose.yml`（#282）は触らない。
- **Tier 3（実基盤依存・対象外）**: Hetzner 実 k3s デプロイ・実 egress IP・リージョンレイテンシ実測・
  月次インフラ費実額・稼働率99%の実測。受け入れ基準の 3 項目（Hetzner 稼働／GitOps 実デプロイ／実測記録）は
  Tier 3 で**本 PR では充足しない**。

## 設計

- ArgoCD manifest は MSP の `deploy/argocd/` を範として AST 用に写像する。`AppProject.namespaceResourceWhitelist`
  は AST チャートが実際にレンダリングする種別（Deployment/Service/ConfigMap/CronJob/PersistentVolumeClaim/
  ExternalSecret）に限定する（最小権限）。`Application.spec.source.path=deploy/helm/ai-stock-trading`。
- External Secrets の `ast-secrets` 同期は **`dataFrom.extract`**（単一 Vault KV から全プロパティ吸い上げ）で行う。
  Vault 側プロパティ名 = Secret キー名（`finnhub-api-key` 等）に一致させる。欠けたキーは同期されず、消費側は
  `optional: true` で許容（fail-safe）。同期先 Secret 名は `ast-secrets`（現行と一致）。
- 既定オフの徹底: `externalSecrets.enabled` と `externalSecrets.appSecrets.enabled` の双方が true のときのみ
  `ast-secrets` ExternalSecret を描画する。ストア（ESO/Vault）が無いクラスタでの誤有効化は `helm` の `fail` で止める
  （受け口の既存挙動を踏襲）。

## 受け入れ基準

- [ ] `deploy/argocd/`（AppProject/Application/README）が追加され、`kubectl apply --dry-run=client` で妥当
- [ ] `externalSecrets.appSecrets.enabled=true` で `ast-secrets` の `ExternalSecret` が描画され、既定オフでは描画されない
- [ ] External Secrets manifest・values・docs に**平文の秘密が無い**
- [ ] `helm lint` と `helm template`（既定オフ／opt-in の両条件）が通る
- [ ] `docs/observability/observability.md`・`docs/infra/infra.md`・`docs/operations/vault-secrets-runbook.md` が
      OTLP 経路・Tier 境界・Vault opt-in 手順を記す
- [ ] Grafana ダッシュボード JSON が妥当な JSON である
- [ ] Hetzner 実デプロイ・実測・GitOps 実同期は **Tier 3** として PR/issue に明示分離
- [ ] `check-doc-links` が通る（相対リンク切れなし）
