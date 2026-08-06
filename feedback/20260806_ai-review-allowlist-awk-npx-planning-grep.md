---
title: レビュー用 allowedTools に awk / npx / git -C planning grep が無く、権限拒否 6 件で claude-review が赤くなる（#163 の残り）
type: plan-feedback
status: open
category: その他
related_ids: [NFR]
source_repo: endazon/ai-stock-trading
source_ref: "PR endazon/ai-stock-trading#401 の claude-review 実行ログ（run 31027396832 / job 92379325087・2026-08-06）"
author: endazon (with Claude Code)
created: 2026-08-06
---

# フィードバック: レビュー用 allowedTools の欠落 3 種（awk / npx / git -C planning grep）

> **送付済み（2026-08-06 JST）。** 計画リポジトリへ `plan-feedback` ラベル付き Issue として起票した:
> [endazon/project-planning#216](https://github.com/endazon/project-planning/issues/216)。
> 以降のトリアージ・裁定は当該 Issue で行う。本書は実装リポジトリ側の控えである。

## 種別

その他（AI レビューの実行環境。**キット配布物と実装リポの両方に同じ欠落がある**）。

## 起点となる計画書

- 機能要求（FR）: —
- 非機能要件: NFR（AI を前提とした開発運用）
- 計画書リンク: `tools/impl-handoff-kit/repo-template/.github/workflows/claude-code-review.example.yml`

## 現状（計画書の記述 / As-Is）

`claude-code-review.example.yml` の `--allowedTools` は、submodule の履歴参照として
`git -C planning` の **`log` / `show` / `diff` / `ls-tree` / `rev-parse` の 5 つ**を許可し、
汎用コマンドとして `grep` / `sort` / `cat` / `head` / `tail` / `cmp` / `diff` / `echo` / `which` を許可する。
Node 系は `node` / `npm ci` / `npm run` / `npm --prefix` を許可する。

**次の 3 種は許可リストに無い。**

| 拒否されたもの | 状況 |
| --- | --- |
| `git -C planning grep` | `git -C planning` は 5 サブコマンドのみ許可。**`grep` は入っていない** |
| `awk` | 汎用コマンドの列に無い（`grep` / `sort` は [#163](https://github.com/endazon/project-planning/issues/163) で追加済み） |
| `npx` | Node 系は `npm` のみ。**`npx` は無い**（`markdownlint-cli2` 等の実行手段が無い） |

> **注意（誤検出しやすい点）**: ファイル内をそのまま `grep 'Bash(npx'` で調べると**コメント行にヒットする**。
> 同ファイルには「他スタック例: `Bash(npm run:*)` / `Bash(npx vitest:*)` / `Bash(pytest:*)` / `Bash(go test:*)`」という
> **例示のコメント**があるためである。実際の許可リストを見るには `--allowedTools "..."` の文字列を取り出して
> 突き合わせる必要がある（本記録はそれで確認した）。

## 問題点 / あるべき姿（To-Be）

### 実測（[endazon/ai-stock-trading#401](https://github.com/endazon/ai-stock-trading/pull/401)・2026-08-06）

`claude-review` ジョブが **`permission_denials_count: 6`** で終わり、`Check permission denials` が
**exit 1 でジョブを赤にした**。内訳は次のとおりである。

| 拒否 | 件数 | 許可リストで直せるか |
| --- | --- | --- |
| `Bash(git -C planning grep)` | 3 | **直せる** |
| `Bash(awk ...)` | 1 | **直せる** |
| `Bash(npx)` | 1 | **直せる** |
| `Bash(which \| npx)` | 1 | **直せる**（前段 `which` は許可済み。後段 `npx` が原因） |
| リダイレクト（`>`）を含む形 | 1 | 直せない（レビュー用は書き込み手段を持たない設計。プロンプト側で対処） |

**レビュー本文は「実行できなかったこと」に `markdownlint-cli2` の拒否を明記しており、報告の作法は守られている。**
問題は次の 2 点である。

1. **ジョブが赤くなる。** 指摘が「重大 0 件・推奨 0 件」であっても `Check permission denials` が落ちるため、
   **必須チェックとして扱えない**。「赤いが問題ない」状態が常態化すると、本当の失敗が埋もれる。
2. **検証が実際に行われない。** `markdownlint-cli2` を実行できないため、レビューは lint の通過を
   **CI の結果を読む形でしか確認できない**。`git -C planning grep` が無いため、計画書 submodule の
   全文検索という最も基本的な裏取り手段も使えない（`git -C planning show` でファイルを名指しできる場合しか読めない）。

### あるべき姿

`--allowedTools` へ次の 3 つを追加する。いずれも**読み取り専用**であり、レビュー用の権限設計を広げない。

```text
Bash(git -C planning grep:*),Bash(awk:*),Bash(npx markdownlint-cli2:*)
```

- **`git -C planning grep`**: 既存の 5 サブコマンドと同じ「相対パス `planning` を含む前方一致」の形にそろえる。
  `Bash(git -C:*)` の一括許可は書き込み系まで通るため採らない（同ファイルのコメントが明示している方針）。
- **`awk`**: `grep` / `sort` と同じ読み取り専用の整形手段である。
- **`npx`**: **`npx markdownlint-cli2:*` のように実行するツール名まで含めて絞る**。裸の `Bash(npx:*)` は
  任意パッケージの取得・実行を許すため採らない。他スタックへ配布する雛形としては、コメントの
  「他スタック例」に `Bash(npx <tool>:*)` は**ツール名まで書く**旨を添えるとよい。

## 実装で判明した経緯

環流記録 5 件の送付（[endazon/ai-stock-trading#401](https://github.com/endazon/ai-stock-trading/pull/401)）に対する
`claude-review` の実行で発生した。**同 PR の差分は `feedback/*.md` のみであり、本件は差分に起因しない。**
レビュー結果そのものは重大 0 件・推奨 0 件・軽微 1 件で、指摘の質に問題は無い。

## 提案（計画への反映案）

- 反映先候補: **`tools/impl-handoff-kit/repo-template/.github/workflows/claude-code-review.example.yml`** の
  `--allowedTools` へ 3 エントリを追加する。あわせて実装リポ側（`.github/workflows/claude-code-review.yml`）へ複製する
  （同ファイルのヘッダが求める 3 系統同期）。
- 本件は [#163](https://github.com/endazon/project-planning/issues/163)（`grep` / `sort` の欠落と `git -C` の
  planning 決め打ち）の**残り**である。#163 は汎用 `grep` を足したが、**`git -C planning grep` は足していない**。

## 影響範囲

- **`.github/workflows/` は GitHub App 権限では編集できない。** 本件の修正はローカル（`workflow` スコープを持つ認証）
  からのコミット/プッシュが要る。キット側（計画リポ）の `*.example.yml` は通常のドキュメント変更として扱えるため、
  **先にキットを直し、実装リポへは人手で複製する**流れになる。
- 同じキットから生成された `endazon/microservices-platform` 側にも同じ欠落があると見込まれる（未確認）。

## 送付状況

送付済み（本文冒頭の注記を参照）。
