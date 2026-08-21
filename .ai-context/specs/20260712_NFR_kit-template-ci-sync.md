---
title: kit テンプレート更新（planning PR #21）の CI・スクリプトへの反映（doc-links planning 検査ほか）
type: spec
status: review
related_ids:
  - NFR
  - IADR-0047
author: claude
created: 2026-07-12
updated: 2026-07-12
plan_refs:
  - "https://github.com/endazon/project-planning/pull/21（impl-handoff-kit repo-template の更新）"
---

# 仕様書: kit テンプレート更新の CI・スクリプトへの反映

> issue #104 の解消を含む。impl-handoff-kit（planning PR #21 マージ済み）で確定した
> テンプレート更新を、kit から生成された本リポジトリへ反映する。

## 起点となる計画書（トレーサビリティ）

- 起点: NFR（CI ゲート・ドキュメント整合）・issue #104
- 参照: planning PR #21（kit repo-template）・platform IADR-0058（doc-links planning 検査方式）

## 目的・背景

1. **planning リンクの CI 検査の欠落（#104）**: PR CI の doc-links は submodule 未取得のため
   planning 配下への破損リンクを検出できない。platform で実証済みの方式（IADR-0058 =
   トークン付き・定期の専用ジョブ）が kit の雛形（`doc-links-planning.example.yml`）になったため適用する。
2. **restore の自動発見化**: kit 更新で restore 系（security / copilot-setup / setup.sh）が
   slnx/sln 自動発見ループになった。特に copilot-setup-steps.yml の旧判定
   （`ls *.sln **/*.csproj`）は現行のユニットレイアウト（`backend/backend.slnx`）で復元対象を
   発見できないため追随する。

## 対象範囲

- 対象:
  - `.github/workflows/doc-links-planning.yml` 新設（kit 雛形の有効化。Secret `PLANNING_REPO_TOKEN` が前提）
  - `scripts/check-doc-links.js` を kit/platform の進化版（`--require-planning` 対応）へ同期
  - `security.yml`・`pr-title.yml`（vulnerable-scan）・`copilot-setup-steps.yml`・`scripts/setup.sh` の
    restore/list を slnx/sln 自動発見ループへ
- 対象外: ci.yml のビルド・テスト（`backend/backend.slnx` 明示のまま維持）。restore 系のみ自動発見形とする方針と IADR-0046 決定 4 との関係は [IADR-0047](../adr/IADR-0047_kit-template-sync-policy.md) で正式化

## 受け入れ基準

- [x] `node scripts/check-doc-links.js` / `--require-planning`（ローカル・planning populate 済み）が通る
- [x] `node scripts/scripts.test.js` が通る（`--require-planning` / `planningPopulated` のテストを追加して回帰確認）
- [x] `bash -n scripts/setup.sh` が通り、自動発見ループが `backend/backend.slnx` を発見する
- [x] doc-links-planning.yml が kit 雛形と同方式（schedule + workflow_dispatch・PLANNING_REPO_TOKEN・--require-planning）

## 計画書との差異

- 差異: なし（kit テンプレートの適用）。

## 未決事項

- Secret `PLANNING_REPO_TOKEN` の登録（メンテナ作業。未登録の間、本ジョブは fail して設定漏れを可視化する）。
