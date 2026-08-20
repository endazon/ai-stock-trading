---
title: Dependabot/submodule トークン配線の修正（PLANNING_REPO_TOKEN 統一）
type: work
status: review
related_ids:
  - NFR
author: claude
created: 2026-07-12
updated: 2026-07-12
plan_refs: []
---

# 仕様書: Dependabot/submodule トークン配線の修正（issue #109 マージ後の失敗対応）

> `20260712_109_dependabot-gitsubmodule.md`（issue #109、PR #111・マージ済み）で `dependabot.yml` に
> `gitsubmodule` エコシステムを追加した後、実際に判明した 2 つの失敗を修正する。原因は
> 「private `planning`（endazon/project-planning）へ自動化がアクセスするトークンの配線」不備。

## 起点・課題

- 起点 ID: NFR（CI/自動化基盤。特定 FR/UC に紐づかないリポジトリ運用整備）
- 対象 Issue: #109（マージ済み PR #111 の後続フォローアップ）
- 横断: project-planning#24（repo-template 側の根本対応。同一パターンを先行実装・PR 作成済み）、
  microservices-platform（同一修正を横展開予定）
- オーナー確認済みの前提（2026-07-12）: `PLANNING_REPO_TOKEN` は **Actions secret かつ Dependabot secret**
  の両方に登録済み。旧 `SUBMODULE_ACCESS_PAT`（Actions secret のみ）は廃止予定で、全ワークフロー参照を
  `PLANNING_REPO_TOKEN` に統一し、オーナーが後で削除する。

### Failure 1: Dependabot が `planning` submodule 更新に失敗（`git_dependencies_not_reachable`）

`dependabot.yml` の `gitsubmodule` エコシステムに、private `planning`（project-planning）を読める
認証情報の紐付けが無かったため、Dependabot が pin 更新 PR を生成できなかった。

### Failure 2: Dependabot PR 上で `claude-review` ジョブが失敗

Dependabot が作成する PR は Dependabot secret のみを参照でき、Actions secret（旧
`SUBMODULE_ACCESS_PAT`）は渡らない。そのため `claude-code-review.yml` の
「Fetch planning submodule」ステップが認証失敗し、`claude-review` ジョブ全体が失敗していた。

> 補足（原因の切り分け）: トークン参照名を `PLANNING_REPO_TOKEN` に変えても、Dependabot PR には依然として
> Actions secret は渡らない（GitHub の制約: Dependabot 起動のワークフローは Dependabot secret と読み取り専用
> `GITHUB_TOKEN` のみを受け取る）。したがって **Failure 2 を実際に解消するのは
> `if: ${{ github.actor != 'dependabot[bot]' }}` によるジョブスキップ**である。トークン名統一の目的は
> (1) 旧 `SUBMODULE_ACCESS_PAT` を削除可能にすること、(2) 通常 PR での planning 取得経路を一本化すること
> であり、Dependabot PR の失敗解消そのものではない。将来 `if` 条件を外す場合はこの制約に留意する。

## 対象範囲・変更内容

### 1. `.github/dependabot.yml`

- 先頭 `version: 2` の直後に `registries` ブロックを追加（`planning-git`、`type: git`、
  `password: ${{secrets.PLANNING_REPO_TOKEN}}`）。
- `gitsubmodule` 更新に `registries: [planning-git]` を紐付け、`planning` submodule への読み取りに
  `PLANNING_REPO_TOKEN`（Dependabot secret）を使わせる。
- 該当コメント（private 権限の説明）を PLANNING_REPO_TOKEN/registries 方式の記述に更新。
- **`planning` は除外しない**（ignore は入れない。目的は planning も自動更新すること、という
  #109 の方針を維持）。

### 2. `.github/workflows/claude-code-review.yml`

- `SUBMODULE_PAT: ${{ secrets.SUBMODULE_ACCESS_PAT }}` → `${{ secrets.PLANNING_REPO_TOKEN }}` に変更し、
  直上コメントの `SUBMODULE_ACCESS_PAT` の記述を `PLANNING_REPO_TOKEN` に更新。
- `claude-review` ジョブに `if: ${{ github.actor != 'dependabot[bot]' }}` を追加し、Dependabot PR
  （機械的な pin bump）では AI レビューをスキップする（本リポにブランチ保護の必須チェック指定は
  無く、スキップしても保護ブランチへのマージ制御には影響しない）。

### 3. `.github/workflows/claude-coding.yml`

- `SUBMODULE_PAT: ${{ secrets.SUBMODULE_ACCESS_PAT }}` → `${{ secrets.PLANNING_REPO_TOKEN }}` に変更し、
  直上コメントを更新。`@claude` メンション起動のみでスキップ条件は不要（Dependabot PR 上で
  自動起動するトリガではないため）。

## 設計判断・非採用の理由

- project-planning#24（repo-template 側）で先行実装済みの同一スニペットをそのまま踏襲した。
  実装リポ側は `.example` 拡張子を持たない実ファイル名（`.github/dependabot.yml` /
  `claude-code-review.yml` / `claude-coding.yml`）である点のみ repo-template と異なる。
- 新たな認可モデル（fine-grained PAT の権限分離方針そのもの）は #109 実装時点で確定済みであり、
  今回はトークン参照名の統一と Dependabot 向け registries 配線のみのため、新規 IADR は起票しない。

## 受け入れ条件

- [x] `.github/dependabot.yml` に `registries.planning-git` が追加され、`gitsubmodule` 更新に
      `registries: [planning-git]` が紐付いている
- [x] `planning` を除外していない（ignore なし）
- [x] `claude-code-review.yml` / `claude-coding.yml` の `SUBMODULE_PAT` 参照が
      `secrets.PLANNING_REPO_TOKEN` に統一されている
- [x] `claude-review` ジョブが Dependabot PR（`github.actor == 'dependabot[bot]'`）でスキップされる
- [x] auto-merge 設定を追加していない（既定で人手レビュー必須のまま）
- [x] 3 ファイルとも妥当な YAML である（`npx --yes js-yaml` で個別検証）
- [x] `node scripts/check-commit-messages.js`（`GITHUB_BASE_REF=develop`）が緑
- [x] `node scripts/check-doc-links.js` が緑
- [ ] **マージ後の確認事項**（本 PR のスコープ外・オーナー側でのフォローアップ）:
      実際に Dependabot の `planning` pin 更新 PR が生成されること、および Dependabot PR で
      `claude-review` がスキップされ通常 PR ではレビューが従来通り走ることを実地確認する。
      確認後、旧 `SUBMODULE_ACCESS_PAT`（Actions secret）をオーナーが削除する。

## fail-safe / 制約

- pin 更新は必ず PR 経由。既定で自動マージ無効（auto-merge 設定を追加しない）。
- 保護ブランチ（develop）へ直接 push しない。本変更も作業ブランチ → PR で導入する。**本エージェントは
  マージを行わない**（オーナー判断待ち）。
- 秘密情報の値はコミットしない（secret 名の参照のみ）。
- `claude-review` 指摘対応は本作業のスコープ外（メイン担当が別途対応）。

## 関連

- Refs #109
- 横断: project-planning#24（repo-template 根本対応。同一パターンを先行実装）
- 横断: microservices-platform（同一修正を別作業で横展開予定）
- 先行: `docs/specs/20260712_109_dependabot-gitsubmodule.md`（本修正の前提となった #109 の初期実装）

## 計画書との差異

- 差異なし（計画書に紐づかないリポ運用整備。project-planning の計画書 ID には対応しない）。
