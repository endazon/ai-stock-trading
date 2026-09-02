---
title: インフラ・構成仕様書（AST）
type: infra-spec
status: draft
created: 2026-07-19
updated: 2026-09-03
author: endazon (with Claude Code)
---
<!-- trace:
ids: [NFR-03, NFR-05, NFR-07, NFR-14]
adrs: [ADR-0001, ADR-0006]
iadrs: [IADR-0052, IADR-0060, IADR-0094]
specs: []
issues: [#24, #282]
-->


# インフラ・構成仕様書（AST）

> リポ単位（原則1つ）。AST の稼働環境・デプロイ構成と、ローカル（経路B）／実基盤（Tier 3）の境界を定める。
> 起点は稼働環境として Hetzner を採る計画 ADR である。
> ローカル（経路B）の Vault 秘匿参照・可観測性・GitOps は AST リポ内の opt-in manifest／docs として整備し、
> 共有スタックの stand-up は MSP 側へ分離する。

## 本書が受け持つ範囲

- 技術検討 / 計画 ADR: 稼働環境として Hetzner を採用する決定（k3s 同居・Vault・可観測性）、基盤（platform）再利用の決定
- 非機能要件: 開場時間帯稼働率 99%・認証情報の Vault 秘匿・OTel/Prometheus/Loki 可観測性・月次インフラ費上限
- 実装 ADR: AST の k8s デプロイは Helm chart とし、共有インフラは MSP の platform-infra を参照する（チャート・共有インフラ）／OpenD 本番化は「既定 no-op の整備」として先行し、切替はゲート＋チェックリストで人手に残す（External Secrets 受け口）

## 環境一覧

| 環境 | 経路 | 用途 | 立て方 | 本仕様での扱い |
| --- | --- | --- | --- | --- |
| ローカル compose | 経路A | 単体開発・E2E | `docker-compose.yml`（MSP・#282 管掌） | 触らない |
| ローカル k3d/k3s | 経路B | 連結配線検証 | MSP `scripts/k8s-local-up.sh` ＋ AST チャート | **本 PR のスコープ** |
| Hetzner k3s | 実基盤 | 本番（ペーパー→実弾） | GitOps（ArgoCD） | **Tier 3・対象外** |

## デプロイ構成（AST）

- **チャート**: [`deploy/helm/ai-stock-trading`](../../deploy/helm/ai-stock-trading/README.md)（10 Worker ＋ OpenD ゲートウェイ）。
  共有インフラ（postgres/rabbitmq/keycloak/otel-collector）は MSP `platform-infra` を ExternalName で素の名前解決する。
- **GitOps**: [`deploy/argocd`](../../deploy/argocd/README.md)（`AppProject`/`Application`）。AST チャートを `ai-stock-trading`
  Namespace へ宣言的同期する骨子。**ブートストラップのみ kubectl・以降 Git 同期**。ArgoCD 本体 install は MSP 共有 stand-up。
- **秘匿情報**: 既定は k8s Secret 直（`ast-secrets`・手動作成）。**Vault 化は opt-in**（[`docs/operations/vault-secrets-runbook.md`](../operations/vault-secrets-runbook.md)）。
- **可観測性**: OTLP→otel-collector→Prometheus/Loki/Tempo（[`docs/observability/observability.md`](../observability/observability.md)）。

## Tier 境界（重要・受け入れ基準の充足状況）

本 PR（#24 の AST 分）は **ローカル（経路B）で `helm`/`kubectl --dry-run` により検証できる分**に限る。
実基盤依存は **Tier 3** として明示分離し、本 PR では**充足しない**。

| # | 稼働環境の計画 ADR ／ #24 の受け入れ基準 | 状況 | 根拠・後続 |
| --- | --- | --- | --- |
| 1 | ペーパー構成が Hetzner k3s で稼働し GitOps でデプロイ | **未充足（Tier 3）** | 宣言マニフェスト（`deploy/argocd`）の妥当性まで。実 k3s の実同期は実基盤。**2026-09-03 実測でマニフェスト自体の不具合を検出**（後述） |
| 2 | 認証情報が Git に含まれず Vault で管理 | **部分（受け口 opt-in）** | 受け口・opt-in 配線は本 PR。ストア（Vault/ESO）は MSP stand-up、実運用は Tier 3。**2026-09-03 実測**: ローカル k3s では `externalSecrets.appSecrets.enabled=false`（既定 opt-in のまま）で `ExternalSecret` リソースは 0 件 |
| 3 | リージョン選定根拠（実測値）の記録 | **未充足（Tier 3）** | レイテンシ実測は実 egress 依存。実測後、稼働環境の計画 ADR へ issue 起票で環流。**`scripts/measure-region-latency.sh` を用意した**（Hetzner 契約後に実行） |

### 2026-09-03 実測（ローカル k3s・#24 棚卸し）

- **ArgoCD Application が 2 件とも同期不能（`SYNC STATUS: Unknown`）。** `kubectl -n argocd get applications` は `ai-stock-trading` / `microservices-platform` の両方で HEALTH `Healthy`・SYNC `Unknown` を返し、`status.conditions` に
  `Failed to load target state: ... deploy/helm/ai-stock-trading: app path does not exist` が記録されている。
  **原因は `deploy/argocd/application.yaml` の `targetRevision: main` である。** 本リポジトリの `main` ブランチは
  2026-07-08 の「Initial commit」1 件のみを持つほぼ空のブランチであり、`deploy/helm/ai-stock-trading` を含む
  実装は既定ブランチ `develop` にしかない（`gh api repos/.../contents/...?ref=main` は 404、`?ref=develop` は
  実在を返すことを実測）。**GitOps のブートストラップ検証（受け入れ基準1の一部）は、実 Hetzner 以前にこの
  マニフェスト不整合で止まる。** 是正（`targetRevision` を実際にデプロイするブランチへ揃える、または
  release ブランチ運用を先に決める）は Tier 3 着手前に直しておくべき軽微な修正である。
- **可観測性スタックはローカルで健全に稼働している。** `kubectl -n platform-infra get pods` で
  `prometheus` / `loki` / `tempo` / `grafana` / `otel-collector` / `alertmanager` / `vault` 等 15 Pod が
  すべて `Running`（1/1）。ダッシュボード資産 `deploy/observability/dashboards/` に
  `ai-stock-trading-overview.json` / `ai-stock-trading-business.json` の 2 件が存在する（業務メトリクス。#287）。
- **`ExternalSecret` はローカル k3s に 0 件。** `helm get values ast -n ai-stock-trading -a` で
  `externalSecrets.appSecrets.enabled: false` を確認（既定 opt-in のまま未有効化。秘匿情報は `ast-secrets`
  の手動 Secret 経路が現行）。

### Tier 3（対象外・後続）に含めるもの

- Hetzner の**リージョン選定**（シンガポール／米国東部／欧州）とレイテンシ実測（moomoo OpenD・主要情報源）
- **サイジング実額**と月次インフラ費（上限 5,000 円）の実額確認 → 前提条件（05_trading-assumptions）へ登録
- 稼働率 **99%**（開場時間帯）の実測・ノード固定（OpenD の egress IP 安定。本番化は「既定 no-op の整備」を先行させ、切替はゲート＋チェックリストで人手に残す）
- 海外 IP からの moomoo・各情報源の利用可否確認

これらは実 Hetzner 環境・実 egress を要するため本作業（ローカル配線検証）では扱わない。
