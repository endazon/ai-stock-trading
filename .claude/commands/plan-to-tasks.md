---
description: 計画書の要求/ユースケースを実装タスクのチェックリストに分解する
argument-hint: <FR-xx | UC-xx | プロジェクト名>
allowed-tools: Read, Grep, Glob, TodoWrite
---

引数 `$ARGUMENTS`: 分解対象の ID（`FR-xx` / `UC-xx`）、または計画リポのプロジェクト名（全体分解）。

手順:

1. 計画リポジトリ（既定 `../project-planning`）から対象の計画書を読む。
2. 実装に必要なタスクへ分解する。各タスクに起点 ID を紐づける。
   - 観点: データモデル / 画面・UI / API・ロジック / バリデーション（画面設計の入力規則）/ 例外処理（UC の例外フロー）/ テスト（受け入れ基準）/ 非機能要件（NFR）。
3. TodoWrite でチェックリスト化し、依存関係・優先度（MoSCoW があれば反映）を示す。
4. 各タスクに「関連 ADR」「受け入れ基準」を併記し、実装着手時に参照できるようにする。

補足: 計画リポ側で `tools/impl-handoff-kit/generators/gen-tasks.js` を使うと、未実装タスクの一覧を機械的に生成できる。
