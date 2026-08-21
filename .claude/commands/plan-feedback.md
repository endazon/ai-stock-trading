---
description: 実装中に判明した計画書の問題を計画リポジトリへ GitHub issue で環流する
argument-hint: <FR-xx | UC-xx | topic>（対象の起点 ID か概要）
allowed-tools: Read, Grep, Glob, Bash(gh issue list:*), Bash(gh issue create:*), Bash(gh search:*), Bash(git log:*)
---

引数 `$ARGUMENTS`: フィードバック対象の起点 ID（`FR-xx` / `UC-xx` / `SC-xx` / `ADR-xxxx`）または概要。

目的: 実装中に判明した計画書（`project-planning`）の誤り・不足・新たな制約を、**計画リポジトリの
GitHub issue** として起票する。

> **環流は issue へ一本化されている**（資料再編 ADR-0029 決定 5）。**本リポジトリに環流記録ファイルを
> 作らない** —— 旧運用の `feedback/` ディレクトリは撤去済みで、裁定の完了記録は計画リポジトリ側の
> `projects/ai-stock-trading/10_feedback/` に残る。

手順:

1. **重複を先に確認する（必須）。** 同件の既存 issue があれば起票せず、そちらへコメントする。

   ```bash
   gh issue list --repo endazon/project-planning --state all --search "<キーワード>"
   ```

2. `plan-feedbacker` サブエージェントに起草を委譲する（事実の整理と反映案の起草）。
3. 種別を判定する: `要求の誤り` / `要求の不足` / `UC/画面の差異` / `新たな制約(ADR要)` / `用語追加` / `その他`。
4. 計画リポジトリの「実装からの環流フィードバック」テンプレート（`.github/ISSUE_TEMPLATE/feedback.yml`）に
   沿った本文を生成し、起票する。ラベルはテンプレートが `feedback` / `decision-needed` を自動で付ける。

   ```bash
   gh issue create --repo endazon/project-planning --title "[feedback] <要約>" --body-file <file>
   ```

   起票できない環境では、貼り付け用の本文を提示して人間に委ねる（**記録ファイルは作らない**）。
5. 起票した issue の番号・URL と、計画側で想定される反映先（要求更新 / 新 ADR / 用語追加 等）を報告する。
   本リポジトリ側で着手が止まる場合は `blocked` ラベルと `docs/blocked-tasks.md` へ記録する。

注意:

- **事実（現状・経緯）と提案（あるべき姿・反映案）を分けて書く。** 計画書の確定変更は計画側
  （`/triage-feedback` と人間）が判断する。
- 本文で本リポジトリの issue / PR を引くときは**フルパス形式**（`endazon/ai-stock-trading#NNN`）にする
  —— 起票先が別リポジトリのため、裸の `#NNN` は計画リポジトリの無関係な issue へ誤リンクする。
- 計画リポジトリのファイルを直接書き換えない。
