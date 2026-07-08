---
description: 関連 ADR の確定済み制約に実装が違反していないか確認する
argument-hint: （省略可）対象モジュールや ADR 番号
allowed-tools: Read, Grep, Glob, Bash(git diff:*)
---

引数 `$ARGUMENTS`: 検査対象（モジュール・差分・特定の `ADR-XXXX`。省略時は変更差分全体）。

手順:

1. `adr-guardian` サブエージェントに検査を委譲する。
2. 計画リポジトリ（既定 `../project-planning`）の `07_adr/` から状態が `Accepted` の ADR を読み、確定済み制約を抽出する（`Superseded`/`Deprecated` は除外し後継を優先）。
3. 実装（または差分）が各制約を遵守しているか確認する。
4. 「違反（重大）/ 逸脱の疑い（要確認）/ 参考」に分類し、根拠 `ADR-XXXX` と `ファイル:行`、対応案を添えて報告する。
5. 制約を変える必要がある場合は「新 ADR の起票が必要」と明記する（既存決定の無断逸脱は禁止）。
