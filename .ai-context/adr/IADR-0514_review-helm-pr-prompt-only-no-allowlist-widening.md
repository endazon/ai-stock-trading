---
title: IADR-0514 Helm の chart を変える PR で AI レビューが試す許可外のコマンドは、許可を広げずにプロンプトで使わせない
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0299, IADR-0145, IADR-0190]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs: []
related_specs:
  - ../specs/20261008_1248_review-helm-pr-denials-prompt.md
---

# IADR-0514: Helm の chart を変える PR で AI レビューが試す許可外のコマンドは、許可を広げずにプロンプトで使わせない

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

## 起点・関連

- 関連する計画書 ID: なし（AI 運用装備の是正。`NFR` 無採番＝工程のメタ作業）
- 関連する実装仕様書: [20261008_1248_review-helm-pr-denials-prompt](../specs/20261008_1248_review-helm-pr-denials-prompt.md)
- 起点 issue: [#1248](https://github.com/endazon/ai-stock-trading/issues/1248)（PR #1245 で `claude-review` が 2 回続けて赤）
- 関連 IADR: [IADR-0299](IADR-0299_review-allowlist-shell-tests.md)（レビューの許可と CI の非対称）・
  [IADR-0145](IADR-0145_permission-denial-fixability-classification.md)（権限拒否の分類）・
  [IADR-0190](IADR-0190_review-verdict-gate.md)（検証の絞り込み）

## コンテキストと課題

PR #1245（Helm の chart と `helm.yml` を変える）で、レビュー本文は完了していたのに `check-permission-denials` が
許容値 4 件を超えてジョブを赤にした（6 件・5 件）。拒否の内訳と原因の推定は作業仕様書の表に置いた。要点は次の 3 つである。

1. `helm version`・`mkdir`・`Write`・リダイレクト・`python3` は、許可に無い（書き込み系は設計上持たない）。
2. `ls` / `head` / `echo` / `which` は許可済みである。`ls` の拒否は**作業ディレクトリ外のパス**が原因と読んだ。
   Claude Code は読み取り系のコマンドでもワークスペースの外を指すパスを拒否する。
3. 2 の引き金はプロンプト自身だった。旧【計画書の場所】節が、submodule の撤去前の前提で
   `planning/` と `../project-planning` を探すよう指示し、「試さずに参照不可と結論するな」とまで書いていた。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | 何もしない | ❌ 再実行でも同じ形で再発した（2 回） |
| B | `Bash(helm version)` を 3 系統へ足す（issue の案 (b)） | ❌ 拒否 8 種のうち 1 種しか消えない。`mkdir` / `Write` / リダイレクト / ワークスペース外のパスは許可では直せない（ワークスペース外への書き込みは許可では直せない。ワークスペース内への書き込みは tag モードの acceptEdits で通ってしまうが、レビューの役割外として prompt で禁じる）。版の確認は検証に要らない |
| C | `check-permission-denials` の許容値を上げる | ❌ 症状を隠すだけ（IADR-0299 の案 E と同じ） |
| **D（採用）** | **プロンプトに Helm の PR 向けの節を置き、使ってよい形・使わない形・陰性対照の代わりを明示する。旧【計画書の場所】節を現状に合わせる** | ✅ 8 種すべてが「AI がその形を選ばなければ起きない」拒否で、代わりの手段が許可済みの範囲にある |

## 決定

### 決定 1: Helm の検証は許可済みの範囲で完結させる

- helm は `helm template` / `helm lint` だけ。`helm version` と `which helm` は使わない（helm の有無は `helm lint` が動けば判る）。
- 一時ファイル・一時ディレクトリを作らない（`mkdir` / `mktemp`・`Write`・`>` `>>`）。**書き込みを伴う検証は設計上許可しない。**
- values を変える陰性対照は `--set` で足りる範囲だけ自分で実走する（`helm template … --set k=v | node scripts/<検査器>.js`）。
- `-f <一時ファイル>` が要る陰性対照は再現せず、**この PR 自身の `Helm` ワークフロー（ジョブ `Lint and render chart`）の結果を引用する**。
- YAML の構文確認は `yq` か `helm lint`（`python3` は許可に無い）。
- `ls` は引数つきで使ってよいが、パスはワークスペース内に限り、連鎖させない。

### 決定 2: 旧【計画書の場所】節を書き直す

計画リポは CI のワークスペースに無い。探させず、照合は「未検証（計画リポ参照不可）」と書かせる。
同じ prompt の【検証の実行】節は既にそう書いており、**節どうしの矛盾を解消**した。

### 決定 3: 許可（3 系統）は変えない

`--allowedTools`（`claude-code-review.yml` / `claude-coding.yml`）と `.claude/settings.json` は差分なし。

### 決定 4: 節の存在を試験で固定する

`scripts/scripts.repo.test.js` に 2 件足した: ①節があり主要な語（`helm version` / `which helm` / `mkdir` / `Write` /
リダイレクト / `Lint and render chart` / `--set`）を含み、節が案内するコマンド（`helm template` / `helm lint` / `yq` / `ls` /
`gh run list` / `node`）が `--allowedTools` にある、旧【計画書の場所】の誘導が戻っていない。②その 3 つの陰性対照。
文言の全文は固定しない（言い回しの改善を止めない）。

## 影響

- Helm の PR で `check-permission-denials` が赤になる主因が消える（見込み）。
- prompt は毎 PR 送信されるため、節は短く保った。経緯は YAML コメント側に置いた（#530 の方針）。

## 残余リスク

- **拒否の原因は推定である。** 生のコマンド文字列（`execution_file`）は取得できず、ラベルとレビュー本文から読んだ。
  特に `ls` の 2 件が両方ともワークスペース外のパスだったかは確かめていない。
- **回帰の確認（issue の受け入れ基準 3）は未了。** 本 PR は Helm を変えないため、次に chart を変える PR で
  `Check permission denials` が緑になることを観測して閉じる。
- プロンプトは拒否を確率的に減らすだけで、AI が別の許可外の形を試せば再び拒否は出る。許容値 4 件の範囲なら警告に留まる。
