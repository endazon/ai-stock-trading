---
description: 実装中に判明した計画書の問題を計画リポジトリへフィードバックする
argument-hint: <FR-xx | UC-xx | topic>（対象の起点 ID か概要）
allowed-tools: Read, Grep, Glob, Write, Bash(ls:*), Bash(mkdir:*), Bash(git log:*)
---

引数 `$ARGUMENTS`: フィードバック対象の起点 ID（`FR-xx` / `UC-xx` / `SC-xx` / `ADR-xxxx`）または概要。

目的: 実装中に判明した計画書（`project-planning`）の誤り・不足・新たな制約を、計画側へ環流する。

手順:

1. `plan-feedbacker` サブエージェントに起票を委譲する（記録の作成と反映案の起草）。
2. 種別を判定する: `要求の誤り` / `要求の不足` / `UC/画面の差異` / `新たな制約(ADR要)` / `用語追加` / `その他`。
3. `feedback/TEMPLATE.md` を雛形に、`feedback/<YYYYMMDD>_<概要のケバブケース>.md` を作成する。
   メタ情報（`category`・`related_ids`・`source_ref`=ブランチ/コミット/仕様書・`created`=本日）を埋める。
4. 計画リポジトリへの伝達を**両経路**で用意する。
   - 記録ファイル経路: 作成した記録を計画リポの `draft/feedback/` にコピーする手順を案内する。
   - GitHub Issue 経路: `endazon/project-planning` の「計画へのフィードバック」テンプレートに合わせた
     **Issue 本文（タイトル・本文）を生成**し、貼り付け or GitHub MCP/`gh` での起票を案内する。
5. 作成した記録のパスと、計画側で想定される反映先（要求更新 / 新 ADR / 用語追加 等）を報告する。

注意: 記録は事実と提案を分けて書く。計画書の確定変更は計画側（`/triage-feedback` と人間）が判断する。
