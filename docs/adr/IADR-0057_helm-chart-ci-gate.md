---
title: IADR-0057 Helm chart の CI ゲートは helm 単体で完結させ、既定 disabled のフラグ ON 派生も描画する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0049
author: claude
created: 2026-07-16
updated: 2026-07-16
plan_refs:
  - "../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md (非機能要件: 運用・保守)"
---

# IADR-0057: Helm chart の CI ゲートは helm 単体で完結させ、既定 disabled のフラグ ON 派生も描画する

- 状態: Accepted
- 日付: 2026-07-16
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（運用・保守）
- 関連 ADR: [[IADR-0049]]（実基盤 E2E は既定 CI から分離し `integration.yml` で実走する）
- 関連仕様書: `docs/specs/20260716_129_ci-gates-pr-title-helm.md`
- Issue: #129（本 issue）／#122（AST chart 追加）／参考: microservices-platform#268（サービス Dockerfile の CI 未検証）

## コンテキストと課題

`deploy/helm/ai-stock-trading`（#122）はデプロイ資産だが、`helm lint` / `helm template` を回す CI が無く、
**chart の破損を CI で検出できない**。microservices-platform#268 が「サービス Dockerfile が CI 未検証で
ビルド不能に気づけなかった」のと同型の穴である。

さらに本 chart には fail-safe 既定として、次の**既定 disabled のフィーチャフラグ**がある。

- `tradingCycle.cronjob.enabled: false`（#121。既定は in-process ポーリング IADR-0023 を維持）
- `moomoo.enabled: false`（#13。既定は paper。実発注しない）

この 2 つの `{{- if }}` ブロックは、**既定値の描画では一度も評価されない**。

## 決定

### 1. ゲートは `helm lint --strict` ＋ `helm template` とし、helm バイナリのみで完結させる

`helm template --validate` や `helm install --dry-run=server` は**実 API サーバを要する**ため使わない。
実基盤依存の検査は `integration.yml`（nightly / dispatch）が担うという既存の切り分け（[[IADR-0049]] / #82）を
踏襲し、既定 CI の安定性・速度を損なわない。

### 2. 既定値だけでなく、既定 disabled のフラグを ON にした派生も描画する

**これが本 ADR の核心**。実測（本 issue の作業中にローカルで検証）:

| 破壊箇所 | 既定値の `helm lint --strict` / `helm template` | フラグ ON の `helm template` |
| --- | --- | --- |
| 常に描画されるテンプレート | exit 1（検出できる） | exit 1 |
| **既定 disabled の `tradingCycle.cronjob` の中** | **exit 0（素通し）** ⚠️ | exit 1（検出できる） |

既定値の描画だけをゲートにすると、「**CI は緑なのに、フラグを有効化した瞬間に壊れる**」状態を
許してしまい、本 issue が塞ごうとしている穴（デプロイ資産の未検証）がそのまま残る。
`tradingCycle.cronjob` も `moomoo` も**有効化を前提に置かれた骨子**であり、有効化時に初めて壊れるのでは
ゲートの意味がない。したがって描画する派生を次の 4 通りとする。

1. 既定値（fail-safe 既定そのもの）
2. `tradingCycle.cronjob.enabled=true`（#121）
3. `moomoo.enabled=true`（#13）
4. 全フラグ ON（相互作用）

### 3. `ci.yml` に足さず、独立ワークフロー `helm.yml` にする

- `ci.yml` は複数の作業が同時に触る共有ファイルで競合しやすい。
- 本 issue は `paths: deploy/helm/**` を求めるが、`ci.yml` にパスフィルタを置くと**全ジョブ**に効く。
- .NET CI とデプロイ資産の CI を独立させる（両者は変更頻度も所要時間も異なる）。

## 影響

- 以後、chart の破損（テンプレート構文・値参照の誤り）はマージ前に落ちる。**既定 disabled の
  経路も含めて**担保されるため、#121 / #13 の有効化時に「CI 緑だったのに壊れる」ことがなくなる。
- `deploy/helm/**` に触れない PR では起動しない（CI 資源を消費しない）。
- ブランチ保護での必須チェック指定はリポ設定であり、コード変更では完結しない（`docs/ai-workflow.md` に記載）。

## 代替案と却下理由

| 案 | 却下理由 |
| --- | --- |
| 既定値の `helm lint` のみ | 既定 disabled のテンプレート内部を素通しする（実測で確認）。本 issue の穴が残る |
| `helm template --validate` / `--dry-run=server` | 実 API サーバ依存。[[IADR-0049]] の切り分け（実基盤は integration.yml）に反し、既定 CI が不安定になる |
| `ci.yml` にジョブを追加 | 共有ファイルの競合。パスフィルタが全ジョブに波及する |
| chart-testing（`ct lint`） | 依存（Python/ct）を増やす割に、本 chart 1 個の検査には過剰。helm 単体で足りる |
| `helm unittest` プラグイン導入 | 描画結果のアサーションはまだ要求されていない。まず「描画できること」の担保を優先する |

## フォローアップ（本 ADR のスコープ外）

- **k8s スキーマ適合検査**（kubeconform / kubeval 等）は未導入。本ゲートは「**描画できること**」までを
  担保し、描画結果が k8s の API スキーマに適合するかは検査しない（例: 型違いの `resources` 値は素通しし得る）。
  必要になった時点で別 issue とする。
- パスフィルタをトリガに置いているため、**このワークフローをそのまま必須チェックにすると**
  `deploy/helm/**` を触らない PR で永久 pending になる（GitHub 仕様）。必須チェック化する際は
  microservices-platform の IADR-0067（ジョブ内パス判定＋安定名の集約ジョブ）と同じ変換が要る。
  本 chart は変更頻度が低く、現時点では推奨チェック（レビューでの確認）に留める。
