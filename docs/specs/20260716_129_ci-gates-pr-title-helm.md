---
title: pr-title.yml の是正と Helm chart の CI ゲート追加（Issue #129）
type: spec
status: review
related_ids:
  - NFR
  - IADR-0057
author: claude
created: 2026-07-16
updated: 2026-07-16
plan_refs:
  - "../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md (非機能要件: 運用・保守)"
related_specs:
  - "../adr/IADR-0057_helm-chart-ci-gate.md"
  - "../ai-workflow.md"
  - "../../deploy/helm/ai-stock-trading/Chart.yaml"
---

# 仕様書: pr-title.yml の是正と Helm chart の CI ゲート追加（Issue #129）

> 本仕様書は実装着手前に作成する。計画書（`project-planning`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: —（CI ゲート整備）
- 非機能要件（NFR）: 運用・保守（規約・デプロイ資産の健全性をマージ前に機械検査する）
- 関連ルール: `.claude/rules/traceability.md`「PR タイトル（スカッシュ後件名）の検査」
- 実装判断: [[IADR-0057]]（本作業で起票。Helm chart の CI ゲート方式）
- Issue: #129（本 issue）／Refs #122（PR #123 の claude-review で検出）／参考: microservices-platform#268

## 目的・背景

### 1) `pr-title.yml` が PR タイトル検査になっていない（重大）

`.github/workflows/pr-title.yml` の中身が `security.yml`（gitleaks / dependency-review /
vulnerable-scan）と**完全に同一**で、PR タイトル検査のロジックが存在しない。コピー時の取り違えと
見られる。結果として:

- squash-merge で develop に載る件名（= PR タイトル + `(#番号)`）が**未検査**。
- 中間コミットの `commit-messages` チェックは force-push 禁止のため事後修正できず、
  **PR タイトル検査が最後の砦**だが、それが名ばかりの状態だった。
- `Security` ワークフローが二重定義され、同一ジョブが毎 PR で二重に走っていた（CI 資源の無駄）。

検査ロジック自体は `scripts/check-commit-messages.js` に**既に実装済み**（`--title` 引数 /
`PR_TITLE` 環境変数の単一件名モード → `validateSubject` を再利用）であり、単体テストも
`scripts/scripts.test.js` に存在する。**欠けているのは呼び出す側のワークフローだけ**。

### 2) Helm chart を検証する CI ゲートが無い（推奨）

`deploy/helm/ai-stock-trading`（#122）を追加したが、`helm lint` / `helm template` を実行する
ジョブが無く、chart 破損を CI で検出できない。microservices-platform#268 と同種の
「**デプロイ資産が CI 未検証**」問題である。

## スコープ

### 含む

- `pr-title.yml` を本来の内容（`check-commit-messages.js` の単一件名モード呼び出し）へ是正する。
- `helm lint` ＋ `helm template` を実行する CI ジョブを追加する（`deploy/helm/**` のパスフィルタ）。

### 含まない

- `check-commit-messages.js` の変更（既に `--title` / `PR_TITLE` に対応済み。**再実装しない**）。
- `helm install` / 実クラスタへの適用・API サーバ検証（`helm template --validate` は実基盤依存のため除外）。
- k8s スキーマ適合検査（kubeconform 等）。→ [[IADR-0057]] にフォローアップとして記す。
- `ci.yml` の変更（共有ファイルの競合回避。独立ワークフローで足りる）。

## 設計

詳細は [[IADR-0057]]。要点:

1. **`pr-title.yml`**: `pull_request` の `opened/edited/reopened/synchronize` で起動し、
   `PR_TITLE` 環境変数経由（シェル展開注入を避ける）で `node scripts/check-commit-messages.js` を実行。
   bot 作成 PR は除外（`pull_request.user.type != 'Bot'`）。Revert / `[skip ci]` はスクリプト側が除外する。
2. **`helm.yml`**: `deploy/helm/**` 変更時に `helm lint --strict` ＋ `helm template` を実行する。
   **既定値だけでなく、既定 disabled のフィーチャフラグを ON にした派生も描画する**（下記テスト方針の根拠）。

## テスト方針（TDD）と検証

CI ワークフローは単体テストの対象にならないため、**ゲートが実際に赤を出せることをローカルで先に実証**する。

### 1) `pr-title.yml`（検査ロジックは既存・単体テスト済み）

`scripts/scripts.test.js` に `checkSingleTitle` / `validateSubject` の単体テストが既にある
（規約適合＝0 / 違反＝1 / 空タイトル＝fail-open）。本作業はその**呼び出し配線**の復旧であり、
ロジックは再実装しない。配線は `PR_TITLE` を与えた実行で確認する。

### 2) `helm.yml`（赤 → 緑 をローカルで実証済み）

| 検証 | コマンド | 結果 |
| --- | --- | --- |
| 現状 chart は緑 | `helm lint --strict` / `helm template` | 0 件失敗・25 リソース描画 |
| **赤の実証**: テンプレートを故意に破壊（`.Values.nonexistent.deep`） | `helm lint --strict` | `Error: 1 chart(s) linted, 1 chart(s) failed` |
| **赤の実証**: 同上 | `helm template` | exit 1 |
| **既定値だけでは不足**: 既定 `disabled` の `tradingCycle.cronjob` テンプレート内部を破壊 | `helm lint --strict` / `helm template`（既定値） | **exit 0（素通し）** ⚠️ |
| 同上をフラグ ON で描画 | `helm template --set tradingCycle.cronjob.enabled=true` | exit 1（検出できる） |

**この 4 行目が本ジョブの設計を決めた**。chart には fail-safe 既定として `tradingCycle.cronjob.enabled=false`
と `moomoo.enabled=false` があり、既定値の描画だけではこれらの `{{- if }}` ブロックが一度も評価されない。
つまり「CI 緑なのに有効化した瞬間に壊れる」= 本 issue が塞ごうとしている穴がそのまま残る。
したがってジョブは**フラグ ON の派生も描画する**。

### 3) 実基盤依存の切り分け

`helm template --validate` / `helm install --dry-run=server` は実 API サーバを要するため**使わない**。
本ジョブは helm バイナリのみで完結し、既定 CI の安定性・速度を損なわない（`integration.yml` が
実基盤依存を担う既存方針（#82 / IADR-0049）と同じ切り分け）。

## 受け入れ基準（Issue #129 の提案チェックボックス）

| # | 基準 | 実現 |
| --- | --- | --- |
| 1 | `pr-title.yml` を本来の内容へ修正（`--title` / `PR_TITLE` で `validateSubject` 再利用・`opened/edited/reopened/synchronize` で起動・bot/Revert 除外） | `pr-title.yml` を全面置換 |
| 2 | `helm lint` ＋ `helm template`（AST chart）を回す CI ジョブを追加（`paths: deploy/helm/**`） | `helm.yml` を新規追加 |

## 影響範囲

| ファイル | 変更 |
| --- | --- |
| `.github/workflows/pr-title.yml` | 全面置換（誤って security.yml の複製だったものを本来の内容へ） |
| `.github/workflows/helm.yml` | 新規（chart 検証） |
| `docs/adr/IADR-0057_helm-chart-ci-gate.md` | 新規（設計判断） |
| `docs/adr/README.md` | IADR-0057 を索引へ追加 |
| `docs/ai-workflow.md` | 必須チェックへ `PR Title` を追記（`Helm` はパスフィルタのため推奨に留める。理由は [[IADR-0057]]） |
| `scripts/check-commit-messages.js` | **変更しない**（既に対応済み） |
| `ci.yml` / `security.yml` | **変更しない**（競合回避） |

## リスクと対策

- **`Security` の二重定義解消**: `pr-title.yml` の置換により、これまで二重に走っていた
  gitleaks / dependency-review / vulnerable-scan が `security.yml` の 1 系統に戻る。
  検査の欠落ではない（同一内容が重複していただけ）。
- **既存 PR タイトルが規約外だと新たに赤が出る**: これは本 issue が意図した効果（すり抜けの防止）。
- **必須チェック化**: リポ設定（ブランチ保護）はコード変更では完結しないため、`docs/ai-workflow.md`
  に記載し、設定はリポ管理者が行う。`PR Title` は全 PR で起動するため必須チェックに適する。
  一方 `Helm` はトリガ側パスフィルタを持つため、必須にすると対象外 PR が永久 pending になる
  （GitHub 仕様）。本作業では推奨に留める（[[IADR-0057]] のフォローアップ参照）。
