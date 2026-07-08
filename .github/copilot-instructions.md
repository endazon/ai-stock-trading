# GitHub Copilot 向け指示

このリポジトリは、上流工程リポジトリ `project-planning` で確定した計画書を**実装する**作業リポジトリである。詳細な規約は `CLAUDE.md` / `AGENTS.md` に従う。本ファイルはその要約である。

> 利用可能な AI（プロファイル）は `AI_SETUP.md` で宣言する。GitHub Copilot で実装する場合、Issue を Copilot にアサインすると coding agent が `.github/workflows/copilot-setup-steps.yml` の環境で自律実装する。`.claude/` の hook / スラッシュコマンドは Copilot では動かないため、下記の検証は CI と DoD で代替する。

## 最優先：トレーサビリティ

実装の起点となる計画書の ID を必ず残す。

- ID の種別: `FR-xx`（機能要求）/ `UC-xx`（ユースケース）/ `SC-xx`（画面）/ `ADR-xxxx`（計画ADR）/ `IADR-xxxx`（実装ADR・本リポ `docs/adr/`）。
- 残す箇所: ブランチ名（`feat/FR-012-...`）、コミットメッセージ先頭（`feat(FR-012): ...`）、コード内コメント、PR 本文。

## 実装方針

- **作業着手前に必ず `docs/specs/<YYYYMMDD>_<概要>.md` に作業仕様書を作成し、それに沿って実装する**（仕様書なしで着手しない）。該当する必須仕様書（機能/画面/通信/データ/技術/テスト/運用/セキュリティ）も作成・更新し、重要な実装判断は実装ADR（`docs/adr/`、`IADR-XXXX`）に残す。
- 計画書（`../project-planning` の `projects/<name>/`）に忠実に実装する。
- 計画外の機能追加・過剰な抽象化・起こり得ないケースへの防御的実装を避ける。
- 受け入れ基準（要求）とユースケースのフロー（基本/代替/例外）をテストに写像する。
- ADR で確定した制約（技術スタック・アーキテクチャ等）に違反しない。
- 計画書の誤り・不足・新たな制約を見つけたら、計画リポジトリへフィードバックする（`/plan-feedback`）。

## 検証・完了

- 完了前にビルド/テスト/lint を実行し、`docs/DEFINITION_OF_DONE.md`（完了の定義）を満たす。Claude Code では `/verify` で自動化できる。
- CI（`ci` / `security` / `codeql`）を green にする。これが機械的な品質ゲートとなる。
- 秘密情報をコミットしない。運用全体は `docs/ai-workflow.md` 参照。

## コミット / PR

- 1 コミット = 1 論理変更。先頭に種別（`feat:` `fix:` `refactor:` `test:` `docs:` `chore:`）と起点 ID。
- `main` への直接コミット禁止。作業ブランチ → PR 経由。

## コードスタイル

- `CLAUDE.md` 末尾の「技術スタック別ルール」に従う（命名規約・ビルド/テストコマンド等）。
