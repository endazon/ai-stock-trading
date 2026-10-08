---
title: Helm の chart を変える PR で AI レビューが許可外のコマンドを試して赤になるのを、プロンプトだけで止める
type: spec
status: accepted
related_ids: [NFR, IADR-0514, IADR-0299, IADR-0145, IADR-0190]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs: []
---

# 仕様書: Helm の chart を変える PR で AI レビューが許可外のコマンドを試して赤になるのを、プロンプトだけで止める（#1248）

## 起点となる計画書（トレーサビリティ）

- 起点: 無採番 NFR（AI レビューの運用。メタ作業）
- 関連 IADR: IADR-0514（本件の決定）・IADR-0299（レビューの許可と CI の非対称）・IADR-0145（権限拒否の分類）・IADR-0190（検証の絞り込み）
- 起票: #1248（PR #1245 の `claude-review` が `Check permission denials` で 2 回続けて赤）

## 目的・背景

Helm の chart を変える PR #1245 で、レビュー本文は完了していた（🔴・🟡 なし）のに、
`check-permission-denials.js` が許容値（4 件）を超える拒否でジョブを赤にした。1 回目 6 件・2 回目 5 件。

## 制約（依頼者の指定）

- **プロンプトだけで直す**（issue の案 (a)）。`--allowedTools` と `.claude/settings.json` は変えない
  （3 系統一致の規約。`check-ai-workflow-config.js` が見ている）。
- プロンプトだけで直せないと判ったら、許可を広げずに止めて報告する。

## 母集合（規則 9: 拒否の実物から引いた）

拒否の一覧は `check-permission-denials` のアノテーション（check-run `113078264147` / `113080923075`）から取った。
ジョブログ本体と `execution_file` は取得できない（ログは別ホストへのリダイレクトで、このセッションの gh は追わない）。
したがって**生のコマンド文字列は手元に無く、ラベル（各セグメントの先頭トークン）とレビュー本文から推定した**。

| ラベル | 回 | 許可の状況 | 拒否の原因（推定と根拠） |
| --- | --- | --- | --- |
| `Bash(which \| helm version \| head)` | 1・2 | `which`・`head` は許可済み。`helm version` は無い | 後段の `helm version`。パイプは各セグメントを個別に判定する |
| `Bash(helm version)` | 2 | 無い | 許可外（`helm template:*` / `helm lint:*` だけ） |
| `Bash(mkdir \| printf \| cat)` | 1 | `cat` だけ許可済み | `mkdir` が無い。1 回目の「リダイレクト 1 件」は `hasRedirect` がファイルへの `>` だけを数えるので、`printf … > <一時ファイル>` を含むこの鎖と読める |
| `Bash(mkdir)` | 1 | 無い | 許可外 |
| `Write` | 2 | 無い | ワークスペース外への書き込みは拒否される（内側は acceptEdits で通るが、レビューの役割外として prompt で禁じる） |
| `Bash(python3 \| yaml.safe_load \| …)` | 1 | 無い | `python3` が許可外（`-c "…; …"` の `;` で分割された姿） |
| `Bash(ls)` | 1・2 | **`Bash(ls:*)` は許可済み** | 下記 |
| `Bash(ls \| head \| echo)` | 1・2 | **3 つとも許可済み** | 下記 |

### `ls` が許可済みなのに拒否された理由

- `labelOf` は `ls` の 2 トークン目以降（引数）を落とすので、`Bash(ls)` は「引数つきの `ls`」を含む。
- `ls | head | echo` の 3 つは全部許可済みで、1 回目のリダイレクト（ファイルへの `>`）は上の `mkdir` の鎖の 1 件で数え終わっている。
  したがって**コマンド名でもリダイレクトでもない原因**がある。
- 残るのは**パスの検査**である。Claude Code は `ls` / `cat` 等の読み取り系でも、作業ディレクトリ（CI の checkout）の外を指すパスを拒否する。
  2 回目のレビュー本文は「`../project-planning` はセッションの作業ディレクトリ外として参照を拒否された」と書いている。
- 引き金はプロンプト自身である。【計画書の場所】節が「`planning/projects/<name>/` は CI で populate 済み」
  「`../project-planning/projects/<name>/` を試せ」「1 を試さずに参照不可と結論してはならない」と指示していた。
  submodule は資料再編（ADR-0029 決定 2）で撤去済みで、同じプロンプトの【検証の実行】節は「参照できない」と書いており、**節どうしが矛盾していた**。
  `/tmp` 側の `ls`（一時ディレクトリの確認）も同じ理由で拒否されうる。

### プロンプトで直せるかの判定

8 種すべて「AI がその形を選ばなければ起きない」拒否である。足りない能力は無い:

- helm の有無と版の確認 → `helm lint` / `helm template` が動けば足りる。
- `--set` だけで作れる陰性対照 → `helm template … --set k=v | node scripts/<検査器>.js` で実走できる（どちらも許可済み）。
- `-f <一時ファイル>` が要る陰性対照 → この PR 自身の `Helm` ワークフロー（ジョブ `Lint and render chart`）の結果を引用する。
  2 回目のレビューは実際に `mcp__github_ci__get_workflow_run_details` で確かめていた（拒否なし）。
- YAML の構文確認 → `yq`（許可済み）か `helm lint`。
- 計画リポ → そもそも CI に無い。探さない。

**結論: プロンプトだけで直せる。許可は広げない。**

## 変更

1. `claude-code-review.yml` の prompt の【計画書の場所】節を、現状（CI に計画リポは無い）に合わせて書き直す。
   `planning/`・`../project-planning` を `ls` / `Read` で探させない。
2. 【検証の実行】節の末尾に【Helm の chart を変える PR】ブロックを足す。
3. 経緯は prompt ではなく YAML コメント側へ書く（prompt は毎 PR 送信される。#530 の方針）。
4. `scripts.repo.test.js` に固定の試験を 1 件足す: ブロックの存在・案内する `helm template` / `helm lint` が許可にあること・
   旧【計画書の場所】の誘導の決まり文句（`CI ではこちら`・`1 を試さずに`）が prompt に戻っていないこと（`../project-planning` という文字列自体は新しい節が「探さない」として含むため、検査の鍵にはしない。#1249 監査 F4）。
5. IADR-0514 を起こし、索引へ行を足す。

## 受け入れ基準

- [x] prompt に Helm の PR 向けの明示ブロックがあり、`helm template` / `helm lint` だけを使う・`helm version` / `which` / `mkdir` / `Write` / リダイレクトを使わない・陰性対照は PR 自身の Helm ワークフローの結果を引用する・`ls` の使い方、を書いている。
- [x] 一時ファイルを作る検証はレビューでは行わない（ワークスペース外は拒否・内側は役割外）旨を prompt に明記している。
- [x] `--allowedTools` と `.claude/settings.json` は差分なし。
- [x] 固定の試験が、ブロックを消すと赤になる（陰性対照を手元で確認）。
- [ ] 回帰の確認（Helm の chart を変える PR で `Check permission denials` が緑）は、本 PR のマージ後に次の Helm PR で観測する。本 PR 自身は Helm を変えないので確かめられない。

## 範囲外

- `check-permission-denials.js` の許容値・分類の変更（症状を隠すだけ。IADR-0299 の案 E と同じ理由で採らない）。
- `--allowedTools` への `Bash(helm version)` の追加（issue の案 (b)。IADR-0514 で不採用の理由を書く）。
