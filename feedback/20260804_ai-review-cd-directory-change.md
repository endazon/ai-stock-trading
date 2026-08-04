---
title: AI ワークフローのプロンプトに「`cd` でディレクトリを移動しない」が無く、`cd planning && git log` が拒否される（実測 4 件）— impl-handoff-kit への追記提案
type: plan-feedback
status: open
category: その他
related_ids:
  - NFR
source_repo: ai-stock-trading
source_ref: "fix/NFR-ai-workflow-readonly-tool-parity / docs/specs/20260804_ai-workflow-readonly-tool-parity.md / endazon/ai-stock-trading#371 の claude-review 実行ログ"
author: endazon (with Claude Code)
created: 2026-08-04
---

# フィードバック: 実行できない形の列挙に `cd` が無い

> **送付済み（2026-08-05）。** 計画リポジトリへ `plan-feedback` ラベル付き Issue として起票した:
> [endazon/project-planning#196](https://github.com/endazon/project-planning/issues/196)。
> 末尾の `mcp__github__actions_list` の実在確認依頼も同 Issue に含めた。
> 以降のトリアージ・裁定は当該 Issue で行う。本書は実装リポジトリ側の控えである。

## 種別

その他（キットのプロンプトの不足。実測に基づく追記提案）。**決定への異議ではない。**
issue [#163](https://github.com/endazon/project-planning/issues/163) /
[#168](https://github.com/endazon/project-planning/issues/168) が確立した
「許可リストで表現できない形はプロンプト側の手順制約で塞ぐ」という方針**そのもの**に沿った、
列挙漏れ 1 件の報告である。

## 起点となる計画書

- 対象: `tools/impl-handoff-kit/repo-template/.github/workflows/claude-code-review.example.yml`
  （`prompt:` 内「次は原理的に実行できない」の箇条書き）／
  `claude-coding.example.yml`（`--append-system-prompt`）
- 関連 issue: [#163](https://github.com/endazon/project-planning/issues/163)（読み取り系ツールの穴と非対称）/
  [#168](https://github.com/endazon/project-planning/issues/168)（実走を求める指示には前提操作の許可・注記を同時に置く）
- 関連 PR: [#176](https://github.com/endazon/project-planning/pull/176)

## 現状（計画書の記述 / As-Is）

キットのレビュー用プロンプトは「**次は原理的に実行できない。試みる必要はなく、必要なら未検証と書くこと**」として
次を列挙している。

- 環境変数の前置き（`VAR=1 cmd` / `export VAR=1`）
- ファイルを書き換える検証
- シェルのループ・複合形（`for` / `while` / `if … then … fi`）
- パイプの各コマンド個別判定
- 出力のリダイレクト（`>` / `> /dev/null`）
- プロセス置換 `<(…)`・コマンド置換 `$(…)`
- 長い連鎖のワンライナー

**この一覧に `cd` が無い。** また許可リスト側にも `Bash(cd:*)` は無い（**入れるべきではない**。`cd` を許すと
以降の相対パス判定が崩れ、`git -C` をパスごとに限定列挙している設計〔#163〕の意味が失われる）。

一方でプロンプトは「`git -C planning` の `log` / `show` / `diff` / `ls-tree` も使える」と**代替手段は案内している**。
しかし「`cd` を使うな」とは書いていないため、AI が `cd planning && git log …` を選ぶ余地が残っている。

## 問題点 / あるべき姿（To-Be）

### 実測（推測ではない）

ai-stock-trading の PR [#371](https://github.com/endazon/ai-stock-trading/pull/371) の `claude-review` 実行で、
**権限拒否が 7 件**発生してジョブが失敗した。うち **4 件が `Bash(cd | git log)`** である。

```
AI の実行中にツールの権限拒否が 7 件発生した…: Bash(cd | git log)（4 件） /
mcp__github__get_issue（2 件） / Bash(ls | head)（1 件）
```

`Bash(git -C planning log:*)` は許可済みであったにもかかわらず、AI は `cd planning && git log` の形を選んだ。
**先頭トークンが `cd` になるため、後続が許可済みでも鎖全体が拒否される。**

### なぜ「代替手段の案内」だけでは足りないか

#168 が確立した設計原則は「**実走を求める指示を書くときは、実走の前提操作を同時に許可・注記する**」である。
本件はその裏面にあたる。**使える手段を書いても、使ってはいけない手段を書かなければ AI はそちらを選び得る。**
現に他の 6 形（環境変数の前置き・ループ・リダイレクト等）は**禁止として明記**されており、`cd` だけが
「代替はあるが禁止と書いていない」状態になっている。列挙の粒度が揃っていない。

さらに悪いのは**失敗の出方**である。ジョブは AI レビュー本文を出したうえで `Check permission denials` が
exit 1 にするため、**レビュー内容には問題が無いのに PR が赤くなる**。同じ内容の PR でも AI の経路次第で
赤/緑が変わる（同セッションの別 PR 2 件は緑だった）ため、**間欠的で原因が読みにくい**。

## 実装で判明した経緯

ai-stock-trading では PR #371 の失敗を受けて、暫定的に次を入れた
（作業仕様書 `docs/specs/20260804_ai-workflow-readonly-tool-parity.md`）。

- レビュー用プロンプトの「原理的に実行できない」一覧へ `cd` の項を追加
- 実装用の `--append-system-prompt` へ同旨の 1 文を追加

**これはキットに無い差分であり、次回のキット同期で失われる。** 本フィードバックはそれを防ぐためのものである。

## 提案（計画への反映案）

- 反映先候補: `claude-code-review.example.yml` の `prompt:` 内一覧へ 1 項目追加（主）／
  `claude-coding.example.yml` の `--append-system-prompt` へ同旨の 1 文（従）。**新 ADR は要さない**
  （既存方針の適用範囲を 1 件広げるだけであり、決定の変更ではない）。
- 提案する文案（レビュー用の一覧へ）:

  > - **`cd` によるディレクトリ移動**（`cd planning && git log …` の形を含む）。`cd` は許可リストに無いため、
  >   後続が許可済みでも先頭で拒否される。submodule の履歴を見るときは
  >   **`git -C <パス> log / show / diff / ls-tree`** を使うこと（許可済み）。

- 併せて、`genericBashDrift` と同じ発想で**プロンプト側の制約一覧にも 3 系統の非対称が生じ得る**ことを
  HOWTO へ注記いただけると、同型の列挙漏れを次回以降で見つけやすくなる（現状 `check-ai-workflow-config.js` は
  許可リストだけを見ており、プロンプトの文面は検査対象外である）。

## 追記: `mcp__github__actions_list` の実在確認のお願い（別論点）

PR #176 が 3 系統へ追加した GitHub MCP ツール 5 種のうち、**`mcp__github__actions_list` だけ実在を
確認できていない**。ai-stock-trading の AI レビューが次を報告した（2 回の実行で同一指摘）。

> `select:mcp__github__actions_list` でも `"github actions list workflow runs"` の全文検索でもヒットせず、
> 実在確認できるのは `mcp__github__list_workflow_runs` 等の granular 名のみだった

これはレビュー実行側のツールレジストリでの観測であり、**CI が pin する github-mcp-server v0.17.1 の
ツール一覧そのものを見たわけではない**（本リポジトリ側では確認手段が無い）。したがって
「実在しない」と断定はしない。ただし、

- 本件（旧名の併記）はまさに「**存在しないツール名だけを許可していたため拒否された**」ことへの対処であり、
  その対処で新たに存在しないかもしれない名前を足しているなら、同じ穴を作ることになる
- 実害は小さい（許可リストに死んだエントリが 1 つ増えるだけで、拒否は起きない）が、
  **死んだエントリは「気付けない」形で残る**——これは #163 が問題視した性質そのものである

**MSP 側で v0.17.1 の実ツール一覧を確認した実績があるはずなので、`actions_list` が実在するかを
確認いただきたい。** 実在しないなら 3 系統から落とすのが正しい（`list_workflow_runs` が既にあるため
機能は失われない）。

## 影響範囲

- **キット**: 上記 2 ファイルへの 1 項目・1 文の追加。既存の決定・許可リストには影響しない。
- **microservices-platform**: 同じプロンプトを持つため同型の拒否が起こり得る。MSP は submodule に
  `src/ai-stock-trading` を持つ構成であり、`cd src/ai-stock-trading && git log` の形が同様に拒否される。
- **ai-stock-trading**: 反映されれば暫定差分を取り下げ、キット準拠へ戻す。
- **判定基準への影響**: 本件が未反映のあいだ、「AI レビューが緑である」ことは
  「レビュー内容に問題が無い」とも「AI が全ての検証を実行できた」とも等価ではない。
  拒否件数はジョブログの `Check permission denials` でのみ読める。
