---
title: IADR-0094 ローカル（経路B）の Vault 秘匿参照・可観測性・GitOps は AST リポ内の opt-in manifest／docs として整備し、共有スタックの stand-up は MSP 側へ分離する
type: impl-adr
status: Accepted
related_ids: [ADR-0006, NFR, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# IADR-0094: ローカル（経路B）の Vault 秘匿参照・可観測性・GitOps は AST リポ内の opt-in manifest／docs として整備し、共有スタックの stand-up は MSP 側へ分離する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-19
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **[ADR-0006](../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md)**
  （Hetzner・Vault 秘匿・OTel/Prometheus/Loki 可観測性）、**NFR**（稼働率99%・認証情報 Vault 秘匿・可観測性）、
  **ADR-0001**（platform 再利用・基盤無改修）
- 対象 Issue: [#24](https://github.com/endazon/ai-stock-trading/issues/24)（インフラ・デプロイ構成）。`Refs #24`
- 関連する実装仕様書: [20260719_adr-0006-local-infra-observability](../specs/20260719_adr-0006-local-infra-observability.md)
- 関連 IADR:
  [IADR-0052](IADR-0052_k8s-helm-chart-shared-infra.md)（AST チャート＝共有インフラを素の名前で解決・OTLP 送出）、
  [IADR-0060](IADR-0060_opend-production-cutover-gates.md)（決定4＝External Secrets 受け口を用意済み・**Vault 化は #24 で未充足**と明記）、
  [IADR-0058](IADR-0058_helm-chart-ci-gate.md)（チャートの `helm lint`/`template` CI ゲート）

> **参照上の注意（ADR 番号の跨ぎ）**: MSP 側の共有 stand-up（`deploy/local/observability`・`vault`・`argocd`）は
> 上流基盤リポ [`../microservices-platform`](../../../microservices-platform) の別採番 IADR（MSP/IADR-0077）で扱う。

## 背景・課題

ADR-0006 は稼働環境を Hetzner とし、NFR で「認証情報の Vault 秘匿」「OTel/Prometheus/Loki の可観測性」「稼働率99%」を求める。
実装リポには [IADR-0060](IADR-0060_opend-production-cutover-gates.md) 決定4 で **External Secrets の受け口**
（`external-secrets.yaml`・既定オフ）だけが用意済みで、values/README がストア整備を **#24 の管掌**と明記している。
一方で #24 の受け入れ基準（Hetzner 稼働・GitOps 実デプロイ・レイテンシ実測）は **実環境（Tier 3）依存**であり、
本セッションでは充足できない。ローカル（経路B・k3d/k3s）で**立てて配線検証できる分**を、実基盤に依存せず前進させたい。

課題は 3 点:

1. どの成果物を **AST リポ**に置き、どれを **MSP** の経路B ハーネス（`scripts/k8s-local-up.sh`・`deploy/local/`）へ置くか。
2. 秘匿参照を Vault へ寄せる際、**平文の秘密をコミットしない**でどう配線するか。既定は現行の k8s Secret 直運用を壊さない。
3. 実基盤依存（Hetzner/実 egress/実測/99%）をどう明示分離するか。

## 決定

### 決定1: 成果物のリポ分割

- **AST リポ（本 PR）**: AST 固有・AST チャートに閉じ、`helm`/`kubectl --dry-run` で検証できる分のみを置く。
  - GitOps: 新規 `deploy/argocd/`（`AppProject`/`Application`/`README`）。AST チャートを `ai-stock-trading` Namespace へ宣言的同期する骨子。
  - 秘匿参照: `external-secrets.yaml` を拡張し、`ast-secrets`（API 鍵群）も Vault から同期できる opt-in を追加。
  - 可観測性: AST サービス向け Grafana ダッシュボード資産（JSON）と `docs/observability` 仕様。
- **MSP リポ（別 PR）**: 共有スタックの実 stand-up（Prometheus/Grafana/Loki/Tempo/Vault+ESO/ArgoCD install）を
  `deploy/local/` の opt-in オーバーレイと `k8s-local-up.sh` の env ゲートで追加する。理由: 経路B ハーネスと共有インフラ
  （otel-collector/postgres 等）は MSP 側にあり、そこでこそ `kustomize build`/`--dry-run` で実検証できる（ADR-0001 の
  基盤再利用に沿う）。`deploy/keycloak/*realm*.json`（realm-fix と衝突）・`docker-compose.yml`（#282）は触らない。

### 決定2: すべて opt-in・既定オフ・fail-safe

- ArgoCD manifest は「ブートストラップのみ kubectl・以降 Git 同期」。既定の経路B 起動には影響しない（適用は任意）。
- External Secrets の `ast-secrets` 同期は `externalSecrets.enabled` **かつ** `externalSecrets.appSecrets.enabled` の
  双方が true のときだけ描画する。既定オフ＝現行の手動 k8s Secret 直運用を維持する。ストア（ESO/Vault）が無いクラスタでの
  誤有効化は `helm` の `fail` で描画時に止める（受け口の既存挙動を踏襲）。

### 決定3: 平文の秘密をコミットしない（`dataFrom.extract`）

- `ast-secrets` の同期は 13 個の鍵を列挙せず、**単一 Vault KV（`ai-stock-trading/app-secrets`）から `dataFrom.extract`**
  で全プロパティを吸い上げる。Vault 側プロパティ名 = Secret キー名（`finnhub-api-key` 等）に一致させる。欠けた鍵は同期
  されず、消費側は `optional: true`（現行）で許容する（fail-safe）。values/manifest/docs には**鍵の実値を一切置かない**。
- moomoo 資格情報・RSA 鍵の受け口（既存の 2 ExternalSecret）は無改修で残す。

### 決定4: 最小権限の GitOps 制約

- `AppProject.namespaceResourceWhitelist` は AST チャートが実際に描画する種別
  （Deployment/Service/ConfigMap/CronJob/PersistentVolumeClaim/ExternalSecret）に限定する。`clusterResourceWhitelist`
  は Namespace のみ。`sourceRepos` は AST リポ 1 本。`Application.spec.source.path=deploy/helm/ai-stock-trading`。

### 決定5: Tier 境界の明示

- Hetzner 実 k3s デプロイ・実 egress IP・リージョンレイテンシ実測・月次インフラ費実額・稼働率99%の実測は **Tier 3**
  （実基盤依存）とし、`docs/infra/infra.md`・PR・issue に**充足しない**旨を明記する。#24 の受け入れ基準 3 項目は
  Tier 3 で本 PR では満たさない（GitOps は「宣言 manifest の妥当性」までを本 PR のスコープとする）。

## 理由

- 受け口（既存）はあるがストアが無い、という中途状態を「AST 固有の opt-in 配線＋docs」と「MSP の共有 stand-up」に
  分けることで、AST PR だけで `helm lint`/`template` により**完全に検証**でき、レビュー単位も小さく保てる。
- `dataFrom.extract` は 13 鍵の列挙・値の混入を避け、平文コミット禁止と fail-safe（欠損は optional 許容）を両立する。
- 最小権限の AppProject は GitOps の誤同期・権限逸脱を構造的に防ぐ（MSP の precedent に一致）。

## 結果

- 良い影響: Vault opt-in・GitOps 骨子・可観測性資産が AST 側に揃い、MSP の共有 stand-up が乗れば経路B で通しで立つ。
  既定オフのため既存の経路B 起動・CI は不変。
- 悪い影響 / トレードオフ: 本 PR 単体では「実際に Vault へ鍵が載り ESO が同期する」「ArgoCD が実同期する」ことは
  検証できない（ストア/ArgoCD install は MSP PR・実運用は Tier 3）。可観測性ダッシュボードの provisioning は MSP の
  Grafana に依存する（AST は資産と docs のみ提供）。
- フォローアップ: (1) MSP PR で共有 stand-up（MSP/IADR-0077）、(2) Tier 3 で Hetzner 実デプロイ・実測・GitOps 実同期、
  (3) 実測結果を [ADR-0006](../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md) へ `/plan-feedback` 環流。
