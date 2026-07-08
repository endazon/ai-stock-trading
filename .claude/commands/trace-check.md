---
description: 計画と実装のトレーサビリティ整合を検査する
argument-hint: （省略可）対象範囲やプロジェクト名
allowed-tools: Read, Grep, Glob, Bash(git log:*), Bash(git diff:*)
---

引数 `$ARGUMENTS`: 検査の対象範囲（省略時はリポジトリ全体）。

手順:

1. `traceability-auditor` サブエージェントに検査を委譲する。
2. 検査観点（`.claude/agents/traceability-auditor.md` 参照）:
   - 計画書に存在するが実装に参照のない FR/UC（実装漏れ）
   - コードが参照するが計画書に存在しない ID（参照切れ）
   - 起点 ID を持たない大きな変更（孤立実装）
3. 指摘を「重大 / 推奨 / 軽微」に分類し、`ファイル:行` または `コミット/PR` の根拠と対応案を添えて報告する。
