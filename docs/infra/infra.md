---
title: インフラ・構成仕様書（AST）
type: infra-spec
status: draft
related_ids:
  - ADR-0006
  - NFR
  - IADR-0094
  - IADR-0052
author: endazon (with Claude Code)
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md"
---

# インフラ・構成仕様書（AST）

> リポ単位（原則1つ）。AST の稼働環境・デプロイ構成と、ローカル（経路B）／実基盤（Tier 3）の境界を定める。
> 起点: [ADR-0006](../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md)（Hetzner）/
> [IADR-0094](../adr/IADR-0094_local-infra-observability-gitops.md)（#24）。

## 起点となる計画書（トレーサビリティ）

- 技術検討 / ADR: [ADR-0006](../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md)（Hetzner・k3s 同居・Vault・可観測性）、[ADR-0001](../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md)（platform 再利用）
- 非機能要件（**NFR**）: 開場時間帯稼働率 99%・認証情報の Vault 秘匿・OTel/Prometheus/Loki 可観測性・月次インフラ費上限
- 実装 ADR: [IADR-0052](../adr/IADR-0052_k8s-helm-chart-shared-infra.md)（チャート・共有インフラ）、[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)（External Secrets 受け口）

## 環境一覧

| 環境 | 経路 | 用途 | 立て方 | 本仕様での扱い |
| --- | --- | --- | --- | --- |
| ローカル compose | 経路A | 単体開発・E2E | `docker-compose.yml`（MSP・#282 管掌） | 触らない |
| ローカル k3d/k3s | 経路B | 連結配線検証 | MSP `scripts/k8s-local-up.sh` ＋ AST チャート | **本 PR のスコープ** |
| Hetzner k3s | 実基盤 | 本番（ペーパー→実弾） | GitOps（ArgoCD） | **Tier 3・対象外** |

## デプロイ構成（AST）

- **チャート**: [`deploy/helm/ai-stock-trading`](../../deploy/helm/ai-stock-trading/README.md)（10 Worker ＋ OpenD ゲートウェイ）。
  共有インフラ（postgres/rabbitmq/keycloak/otel-collector）は MSP `platform-infra` を ExternalName で素の名前解決する（IADR-0052）。
- **GitOps**: [`deploy/argocd`](../../deploy/argocd/README.md)（`AppProject`/`Application`）。AST チャートを `ai-stock-trading`
  Namespace へ宣言的同期する骨子。**ブートストラップのみ kubectl・以降 Git 同期**。ArgoCD 本体 install は MSP 共有 stand-up。
- **秘匿情報**: 既定は k8s Secret 直（`ast-secrets`・手動作成）。**Vault 化は opt-in**（[`docs/operations/vault-secrets-runbook.md`](../operations/vault-secrets-runbook.md)）。
- **可観測性**: OTLP→otel-collector→Prometheus/Loki/Tempo（[`docs/observability/observability.md`](../observability/observability.md)）。

## Tier 境界（重要・受け入れ基準の充足状況）

本 PR（#24 の AST 分）は **ローカル（経路B）で `helm`/`kubectl --dry-run` により検証できる分**に限る。
実基盤依存は **Tier 3** として明示分離し、本 PR では**充足しない**。

| # | ADR-0006 / #24 受け入れ基準 | 状況 | 根拠・後続 |
| --- | --- | --- | --- |
| 1 | ペーパー構成が Hetzner k3s で稼働し GitOps でデプロイ | **未充足（Tier 3）** | 宣言マニフェスト（`deploy/argocd`）の妥当性まで。実 k3s の実同期は実基盤 |
| 2 | 認証情報が Git に含まれず Vault で管理 | **部分（受け口 opt-in）** | 受け口・opt-in 配線は本 PR。ストア（Vault/ESO）は MSP stand-up、実運用は Tier 3 |
| 3 | リージョン選定根拠（実測値）の記録 | **未充足（Tier 3）** | レイテンシ実測は実 egress 依存。実測後 [ADR-0006](../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md) へ `/plan-feedback` 環流 |

### Tier 3（対象外・後続）に含めるもの

- Hetzner の**リージョン選定**（シンガポール／米国東部／欧州）とレイテンシ実測（moomoo OpenD・主要情報源）
- **サイジング実額**と月次インフラ費（上限 5,000 円）の実額確認 → 前提条件（05_trading-assumptions）へ登録
- 稼働率 **99%**（開場時間帯）の実測・ノード固定（OpenD の egress IP 安定・[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)）
- 海外 IP からの moomoo・各情報源の利用可否確認

これらは実 Hetzner 環境・実 egress を要するため本作業（ローカル配線検証）では扱わない。
