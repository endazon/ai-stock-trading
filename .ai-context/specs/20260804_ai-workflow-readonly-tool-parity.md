---
title: 作業仕様書 — AI ワークフローの読み取り系ツール許可を 3 系統でキットへ揃え、`cd` の拒否をプロンプトで塞ぐ
type: work
status: review
related_ids: [NFR]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - planning:tools/impl-handoff-kit/README.md
  - planning:tools/impl-handoff-kit/HOWTO.md
related_specs:
  - ./20260804_claude-coding-git-show-allowance.md
  - ../../docs/ai-workflow.md
  - ../adr/IADR-0047_kit-template-sync-policy.md
---

# 作業仕様書: AI ワークフローの読み取り系ツール許可を 3 系統で揃える

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（AI 実行環境の権限設定。**NFR** 相当）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: なし。**新規 IADR も作らない**（キット由来の許可リスト同期であり、本リポ固有の設計判断を
  行わない。[IADR-0047](../adr/IADR-0047_kit-template-sync-policy.md) の方針に整合）
- 上流の起点: 計画リポの
  [#163](https://github.com/endazon/project-planning/issues/163)（読み取り系ツールの穴と非対称）/
  [#168](https://github.com/endazon/project-planning/issues/168)（実走の前提操作を同時に許可する）と、
  その反映 PR [#176](https://github.com/endazon/project-planning/pull/176)（本作業の時点で **OPEN・未マージ**）

## 目的・背景

本リポジトリの PR [#371](https://github.com/endazon/ai-stock-trading/pull/371) で `claude-review` ジョブが失敗した。
AI レビュー本文は正常に出ている（🔴 0 件）が、後段の安全弁 `Check permission denials` が
**権限拒否 7 件**を検出して exit 1 にしたためである。

```
AI の実行中にツールの権限拒否が 7 件発生した（CI には承認する人間が居ないため、これらの作業は
実行されていない）: Bash(cd | git log)（4 件） / mcp__github__get_issue（2 件） / Bash(ls | head)（1 件）
```

内訳と原因は次のとおりで、**いずれも本 PR の内容とは無関係の既存の穴**である。

| 拒否 | 件数 | 原因 |
| --- | --- | --- |
| `mcp__github__get_issue` | 2 | 許可リストにあるのは統合名 `issue_read` のみ。**アクションが pin する github-mcp-server v0.17.1 に統合名は存在しない**（統合名は v1.x 系で導入）。存在しないツールは AI に提示されないため、AI は旧名 `get_issue` を使い拒否された |
| `Bash(cd \| git log)` | 4 | `Bash(git -C planning log:*)` は許可済みだが、AI が `cd planning && git log` の形を選んだ。**先頭トークンが `cd` になるため鎖全体が拒否**される |
| `Bash(ls \| head)` | 1 | 同上（複合形） |

同じ内容の PR でも AI の経路次第で赤/緑が変わる（同時期の PR #369 / #370 の `claude-review` は緑）。
**間欠的で原因が読みにくい**うえ、赤のときは「レビュー内容に問題がある」と誤読される。

### 3 系統の実測差分

許可リストは「`.claude/settings.json` / `claude-coding.yml` / `claude-code-review.yml`」の 3 系統を
手作業で同期する構造である。上流キット（PR #176 ブランチ）と本リポジトリを突き合わせたところ、
**3 系統とも同一の 10 エントリが欠けており、本リポジトリ固有の追加は 1 つも無かった**（＝厳密な部分集合）。

| 欠けているエントリ | 種別 |
| --- | --- |
| `Bash(grep:*)` / `Bash(sort:*)` / `Bash(which:*)` | 読み取り専用の基本コマンド（#163） |
| `Bash(dotnet --version)` / `Bash(dotnet --info)` | 引数固定形の環境確認（#168） |
| `mcp__github__get_issue` / `get_pull_request` / `list_sub_issues` | GitHub MCP の**旧名**（v0.17.1 に実在するのはこちら） |
| `mcp__github__list_workflow_runs` / `actions_list` | CI 結果の参照（#168） |

`rg` があるから `grep` は不要とは言えない。**パイプは各コマンドが個別に判定される**ため、後段の
`grep` が未許可だと鎖全体が落ちる。

## 対象範囲

- **対象**:
  1. 3 系統すべてへ上記 10 エントリを追加する。追加後の内容は**キットと完全一致**させ、
     後続のキット同期でこの箇所が差分にならないようにする。
  2. `cd` によるディレクトリ移動を禁止する記述を、レビュー用プロンプトと実装用
     `--append-system-prompt` に追加する。
  3. `cd` の件は**キットに存在しない差分**になるため、計画リポへの環流記録を起草する。
- **対象外**:
  - `scripts/check-ai-workflow-config.js` へのキット同期（`genericBashDrift` の取り込み）。上流 PR #176
    が未マージのため取り込まない。取り込みは後続のキット同期 PR で行う。
  - キットのその他の差分（HOWTO・`check-doc-links.js` の `--self-test` 等）。本作業は
    **許可リストと、それに起因する拒否**に限定する。
  - `Bash(cd:*)` の**許可リストへの追加**。採ってはならない（後述）。
  - 環流記録の**送付（Issue 起票）**。起草までとし、送付は別途判断する。

## 設計

### 1. 許可リストはキットと完全一致させる

各ワークフローの `--allowedTools` 行を**キットの同じ行で置き換える**。`.claude/settings.json` は
差分が 10 エントリと `//` 注記だけであったため、**キットのファイルで丸ごと置き換える**（`cmp` でバイト一致を確認）。

本リポジトリ固有の追加が 1 つも無いことを事前に確認済みであり、この置き換えで失われるものは無い。
「キットと完全一致」を保てば、後続のキット同期でこの箇所は差分にならない。

`settings.json` の `//` 注記も更新される。旧注記は GitHub MCP のツール名について
「**現行サーバ準拠（… 旧 get_issue / get_pull_request / create_*_review は廃止名）**」と書いていたが、
これは**事実と逆**である。v0.17.1 に実在するのは旧名の方であり、統合名は無い。
誤った注記を残すと、次に読む人が「旧名は消してよい」と判断して同じ拒否を再発させる。

### 2. `cd` はプロンプトで塞ぐ（許可リストでは塞がない）

**`Bash(cd:*)` を許可リストへ足してはならない。** `cd` を許すと以降の相対パス判定が崩れ、
`git -C` を `.gitmodules` のパスごとに限定列挙している設計（#163）の意味が失われる。
`Bash(git -C:*)` の一括許可を禁じているのと同じ理由である。

キットのレビュー用プロンプトは「原理的に実行できない形」として環境変数の前置き・シェルのループ・
リダイレクト・プロセス置換・長い連鎖を**禁止として明記**しているが、**`cd` だけが抜けている**。
代替手段（`git -C planning log / show / diff / ls-tree`）は案内されているものの、
「`cd` を使うな」とは書かれていないため AI がそちらを選ぶ余地が残っていた。列挙の粒度を揃える。

### 3. キットに無い差分は環流する

2 はキットに存在しない追加であり、放置すると次回のキット同期で失われる。
環流記録 `feedback/20260804_ai-review-cd-directory-change.md`（環流記録）
を起草した（送付は本作業の対象外）。

## 受け入れ基準

- [x] 3 系統の許可リストがキット（PR #176 ブランチ）と一致する。
  - [x] `.claude/settings.json` はキットと**バイト一致**（`cmp` で確認）。
  - [x] 両ワークフローの `--allowedTools` 行がキットの同じ行と**文字列一致**。
- [x] `mcp__github__get_issue` / `get_pull_request` / `list_sub_issues` が 3 系統すべてにある
      （#371 で拒否された 2 件の直接原因）。
- [x] `Bash(cd:*)` を**許可リストに追加していない**。
- [x] `cd` を禁止する記述がレビュー用プロンプトと実装用 `--append-system-prompt` の両方にある。
- [x] `--allowedTools` / `--append-system-prompt` がそれぞれ**1 行・二重引用符 1 組**を保っている
      （改行すると後続行が別引数になり指示が壊れる）。対象は実フラグ **3 行**である
      （`claude-code-review.yml` の `--allowedTools` 1 行、`claude-coding.yml` の
      `--append-system-prompt` と `--allowedTools` の 2 行）。`claude-code-review.yml:190` にも
      `--allowedTools "…"` を含む行があるが、これは**記法を説明するコメント**であって
      フラグではない（検査スクリプトが素朴に文字列一致するため 4 件目として数え上げてしまう）。
- [x] 本リポジトリの `scripts/check-ai-workflow-config.js`、および `genericBashDrift` を持つキット版の
      検査器の**両方**で ERROR 0 件。

## テスト方針

ワークフローの許可設定であり xUnit の対象ではない。検証は検査器の実走と機械的な突き合わせで行う。

| 検証 | 期待 | 実測 |
| --- | --- | --- |
| `.claude/settings.json` とキットの `cmp` | バイト一致 | **バイト一致** |
| 両ワークフローの `--allowedTools` 行とキットの同行の比較 | 完全一致 | **両方とも完全一致** |
| `--allowedTools` のエントリ数（review） | 38 → 48 | **38 → 48** |
| `--allowedTools` のエントリ数（coding） | 45 → 55 | **45 → 55** |
| `--allowedTools` / `--append-system-prompt` の引用符ペア | 実フラグ 3 行が各行ちょうど 2 個 | **3 行とも OK**（改行なし。ほかに検査へ引っ掛かる 1 行はコメント） |
| `node scripts/check-ai-workflow-config.js` | ERROR 0 件 | **問題なし** |
| キット版検査器（`genericBashDrift` 込み）を `--dir` 実走 | ERROR 0 件 | **問題なし** |

`cd` の禁止が実際に効いたかは、本 PR 自身の `claude-review` ジョブが
`Check permission denials` を通過するかで観測できる。ただし AI の経路に依存するため、
**1 回緑になっても「二度と起きない」ことの証明にはならない**（原理的に、拒否は起こさせない側の
制約であって、検査で保証できるものではない）。

## 計画書との差異

- 差異: **あり**。`cd` の禁止はキットに存在しない追加である。環流記録
  `feedback/20260804_ai-review-cd-directory-change.md`（環流記録）
  を起草した。キットへ反映されたら本リポの暫定差分は取り下げ、キット準拠へ戻す。

## 未決事項

- 環流記録の**送付（`plan-feedback` Issue の起票）**は未実施。

## 補足: 本 PR と #369 の関係

本 PR は [#369](https://github.com/endazon/ai-stock-trading/pull/369)（`Bash(git show:*)` の追加）の
ブランチの上に積んでいる。両者は `claude-coding.yml` の**同じ 1 行**を編集するため、develop から
独立に生やすと後にマージする側で必ず衝突するためである。

- **#369 を先にマージすること。** その後 GitHub が本 PR の base を develop へ自動で付け替える。
- 付け替え後に develop が進んでいた場合は、force push を用いず `git merge origin/develop` で解消する
  （CLAUDE.md の破壊的 git 操作の禁止に従う）。
