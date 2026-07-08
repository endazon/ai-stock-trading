---
description: 計画書の要求/ユースケースから実装に着手する
argument-hint: <FR-xx | UC-xx>（起点となる ID）
allowed-tools: Read, Grep, Glob, Edit, Write, Bash(git switch:*), Bash(git branch:*)
---

引数 `$ARGUMENTS`: 実装の起点となる ID（例 `FR-012` または `UC-03`）。

手順:

1. 計画リポジトリ（既定 `../project-planning`、submodule の場合 `planning/`）から該当 ID の計画書を読む。
   - `FR-xx` → `projects/<name>/02_requirements/`（要求・受け入れ基準）と、トレーサビリティ表から関連 UC/画面/ADR を辿る。
   - `UC-xx` → `projects/<name>/03_usecases/`（基本/代替/例外フロー）と関連要求・画面。
2. 関連 ADR（`07_adr/`）を読み、確定済み制約を確認する。曖昧なら実装を止めて確認する。
3. 作業ブランチを作成する（例 `feat/FR-012-<概要のケバブケース>`）。
4. `spec-implementer` の方針で実装し、受け入れ基準を `test-author` の方針でテスト化する。
5. コード内コメント・コミットメッセージに起点 ID を残す（`.claude/rules/traceability.md` に従う）。
6. 実装した内容・テスト・計画書との差異（あれば）を報告する。

注意: 計画書（fixed/Accepted）に反する実装はしない。差異が必要なら根拠を残し人間に委ねる。
