---
title: dependabot.yml に gitsubmodule エコシステムを追加し planning pin の自動更新を有効化する
type: work
status: review
related_ids:
  - NFR
author: claude
created: 2026-07-12
updated: 2026-07-12
plan_refs: []
---

# 仕様書: dependabot.yml への gitsubmodule 追加（issue #109）

> 本リポジトリの `planning` submodule（private `project-planning`）の pin（gitlink）が、計画書の進行に
> 追従して自動更新されるようにする。`.github/dependabot.yml` に `gitsubmodule` エコシステムを追加する。

## 起点・課題

- 起点 ID: NFR（CI/自動化基盤。特定 FR/UC に紐づかないリポジトリ運用整備）
- 対象 Issue: #109（`chore: planning submodule の pin 自動更新を有効化する（Dependabot gitsubmodule）`）
- 上流: microservices-platform#260（追加ユニット submodule pin 自動更新）、project-planning#22
  （`impl-handoff-kit` の repo-template へ `gitsubmodule` を同梱する根本対応。マージ待ち・PR #23）
- 課題: `.github/dependabot.yml` は `github-actions` エコシステムのみ有効で `gitsubmodule` を含まないため、
  `planning` submodule の pin（gitlink）が計画リポの進行に追従せず自動更新されない。

## 対象範囲

- `.github/dependabot.yml`: `github-actions` ブロックの直後に `gitsubmodule` ブロックを追加する。
  `directory: "/"` を指定し、root `.gitmodules` に列挙された全 submodule（本リポでは `planning` のみ）を
  対象にする。**`planning` を除外しない**（計画書の pin 追従が本 issue の主目的のため）。

  ```yaml
  - package-ecosystem: "gitsubmodule"
    directory: "/"
    schedule:
      interval: "weekly"
    open-pull-requests-limit: 5
  ```

  - `open-pull-requests-limit: 5` は明示指定（既定値と同じだが、可読性のため明記）。
  - auto-merge 設定（`.github/workflows/` に auto-merge 用ワークフローを追加する等）は**一切行わない**
    （fail-safe: pin 更新は必ず PR 経由・自動マージ既定オフ）。既存ワークフローを走査したが
    dependabot 用の auto-merge ワークフローは本リポに存在しない。

## 設計判断・非採用の理由

- スニペットは共有コンテキスト（3 リポ横断の submodule pin 自動更新導入タスク）の正準設定をそのまま採用し、
  ローカルでの改変は行っていない。project-planning#22（repo-template 同梱）で同一スニペットが先行実装済みで
  内容が一致していることを確認済み。tool 選定（Dependabot vs Renovate）・`planning` を除外しない方針は
  いずれも issue #109 本文および上記共有コンテキストで既に確定済みの決定であり、本作業で新たな設計判断は
  発生していないため、新規 IADR は起票しない（CLAUDE.md「重要な実装判断」に該当する局所的トレードオフが無い）。

## 受け入れ条件

- [x] `.github/dependabot.yml` に `gitsubmodule` エコシステムが追加され、`directory: "/"` で
      `planning` を含む root `.gitmodules` の全 submodule が対象になっている
- [x] `planning` を除外していない
- [x] auto-merge 設定を追加していない（既定で人手レビュー必須のまま）
- [x] `.github/dependabot.yml` が妥当な YAML である（`npx js-yaml` で検証）
- [x] `node scripts/check-commit-messages.js`（`GITHUB_BASE_REF=develop`）が緑
- [x] `node scripts/check-doc-links.js` が緑
- [ ] **マージ後の確認事項**（本 PR のスコープ外・オーナー側でのフォローアップ）:
      private `planning`（project-planning）に対して Dependabot が実際に read アクセスでき、
      pin 更新 PR を生成できることを確認する。同一アカウント（endazon）の private リポのため既定で
      解決する可能性があるが未実証。アクセス不可の場合は GitHub 側で Dependabot に当該 private リポへの
      最小権限アクセスを許可する設定を行う（IADR-0058: private submodule の CI 取得、IADR-0065: public
      ユニットの CI 取得はトークン不要、の既存方針と整合させる）。

## fail-safe / 制約

- pin 更新は必ず PR 経由。既定で自動マージ無効（`dependabot.yml` に auto-merge 設定を書かない）。
- 保護ブランチ（develop）へ直接 push しない。本変更も作業ブランチ → PR で導入する。
- 秘密情報はコミットしない。

## 関連

- Refs #109
- Refs project-planning#22（repo-template への同梱。先行 PR: <https://github.com/endazon/project-planning/pull/23>）
- Refs microservices-platform#260（追加ユニット submodule pin 自動更新。`planning` + `src/ai-stock-trading` が対象）
- 参考: IADR-0058（private submodule の CI 取得）・IADR-0065（public ユニットの CI 取得はトークン不要）
  ※ 本リポ `docs/adr/` には IADR-0058/0065 は存在しない（microservices-platform 側の ADR 番号のためリンクは
  張らずプレーンテキスト言及にとどめる）。

## 計画書との差異

- 差異なし（計画書に紐づかないリポ運用整備。project-planning の計画書 ID には対応しない）。
