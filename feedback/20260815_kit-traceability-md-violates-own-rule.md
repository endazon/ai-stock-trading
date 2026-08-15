---
title: キットの traceability.md が、自身の定める表記規約に違反している
type: plan-feedback
status: open
category: その他
related_ids: [NFR, IADR-0200]
source_repo: ai-stock-trading
source_ref: docs/specs/20260815_487_cross-repo-ref-notation.md（#487 / PR #514）
author: endazon (with Claude Code)
created: 2026-08-15
dispatched: true
planning_issue: 349
---

# キット配布物が自身の規約に違反している（`planning issue #202`）

## 何が起きたか

[#487](https://github.com/endazon/ai-stock-trading/issues/487) で `check-cross-repo-refs.js` を
置換点つきで実走したところ、**キット配布物である `.claude/rules/traceability.md` 自身が
1 件の違反を出した。**

```
.claude/rules/traceability.md:153  [空白区切りの修飾] planning issue #202  →  planning#202
```

同ファイルは「**規約の書式は詰めた形であり、空白が入ると機械的突合に掛からない**」と定めている。
**その規約を書いている当のファイルが、その規約を破っていた。**

## 🔴 これは検査器のヘッダが予告していた型の 1 段深い版である

`check-cross-repo-refs.js` の冒頭は、この検査器が作られた理由をこう書いている。

> **規約に書くだけでは守られないことが実測で確かめられている。** microservices-platform では
> 規約に反する表記が 158 occurrence 蓄積し、さらに**その規約が書いてある当のファイルを編集する
> PR が同じ違反を犯して CI を green で通過した**。

予告されていたのは「**規約ファイルを編集する PR が違反する**」であった。
実際に起きたのは「**規約ファイルが最初から違反している**」である。

## 実装リポ側での扱い

`.claude/rules/traceability.md` は**分類 A（キット配布物）**であり、
同ファイル冒頭が「直接編集するとバイト一致が崩れ、キットを同期するたびに手動マージが要る」と定めている。

したがって **ai-stock-trading では手元で直さず、検査の除外に入れた。**
除外の理由は `.claude/rules/traceability.repo.md` に全数記載してある。

> 🔴 **この除外は暫定である。** キット側が是正されたら**除外を外すこと**。
> 外し忘れると、キットが直った後も検査が 1 件甘いまま残る（IADR-0200 残余リスクに記載）。

## 依頼した内容（planning#349）

1. `traceability.md:153` の `planning issue #202` を `planning#202` へ是正する
2. あわせて**キット配布物全体を検査器にかける**ことを検討する
   —— 配布物が自身の規約に違反したまま各リポへ配られると、**各リポが除外を積むことになる**

## トリアージ結果

（計画側で記入）

## 関連

- [#487](https://github.com/endazon/ai-stock-trading/issues/487)（表記の確定と検査器の配線）
- [IADR-0200](../docs/adr/IADR-0200_cross-repo-ref-notation.md) 決定3
- planning#318（`check-cross-repo-refs.js` の環流提案）
