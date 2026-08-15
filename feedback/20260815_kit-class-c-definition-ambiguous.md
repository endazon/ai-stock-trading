---
title: 分類 C の定義「置換点を持つ配布物」が曖昧で、同じ取り違えが 2 回起きた
type: plan-feedback
status: open
category: 改善提案
related_ids: [NFR, IADR-0202, IADR-0203]
source_repo: ai-stock-trading
source_ref: docs/specs/20260815_521_kit-sync-and-class-c-audit.md（#521）
author: endazon (with Claude Code)
created: 2026-08-15
dispatched: true
planning_issue: 363
---

# 分類 C の定義を「持つ」から「埋めている」へ

## 事実

キット同期の分類表（`kit-sync-classification.example.json`）は C をこう定めている。

> C = 本リポの中身そのもの（雛形から書き起こした実体、または**置換点を持つ配布物**）。同期しない。

**「置換点を持つ」が、2 通りに読める。**

| 読み方 | 帰結 |
| --- | --- |
| ❌ 置換点を**「持つ」**なら C | **埋めていなくても C**。固有デルタ 0 のファイルが C に入る |
| ✅ 本リポが置換点を**「埋めている」**なら C | 固有デルタがあるものだけが C |

## 🔴 実装リポジトリで 2 回起きた

| # | ファイル | 何が起きたか |
| --- | --- | --- |
| 1 | `.claude/rules/traceability.md` | 置換点を持たないのに C。**キット側の是正 2 件が戻ってこなかった**（planning#349 の表記是正・planning#350 の規則 8） |
| 2 | `scripts/check-cross-repo-refs.js` | 置換点を**一度も埋めていない**（env 注入を選んだ）のに C。**キットが planning#354 で是正した後も、古い写しが `IADR-0140` の誤引用と「ワークフローを編集できない」という誤った前提を保持し続けた** |

**2 件目は、自分が環流した是正が自分に戻ってこなかった形である。**

> 🔴 **分類 C は「同期しない」であるため、置いた瞬間に検査が止まる。**
> **間違えた分類は、間違いを検出する機構ごと無効化する。**

## 依頼

1. **定義を「本リポが置換点を埋めているなら C」へ改める**（`$comment` の 1 行）
2. **判定の手がかりを併記する** —— 実装側で機械化した 2 つの信号を提供する
   - **キット版とバイト一致** → 固有デルタ 0。C の根拠が無い
   - **置換点のプレースホルダが残っている** → 埋めていない。C の根拠が無い
   - **キットに対応物が無いファイルは対象外**（本リポ固有の実体であり C で正しい）
3. **検査器を配ることを検討する**（実装は `scripts.repo.test.js` にある。IADR-0203）

## 実装側の実装で踏んだ注意点（そのまま渡す）

**置換点の目印を文字列としてだけ引くと、偽陽性が出る。**
規約・ADR が**プレースホルダを引用しただけの行**に当たる（実測: 本件の ADR 索引行に当たった）。
**キット版が `【置換点】` を宣言しているファイルに限定する**ことで解いた。

## トリアージ結果

（計画側で記入）

## 関連

- [#521](https://github.com/endazon/ai-stock-trading/issues/521) / [IADR-0203](../docs/adr/IADR-0203_class-c-requires-local-delta.md)
- [#517](https://github.com/endazon/ai-stock-trading/issues/517) / [IADR-0202](../docs/adr/IADR-0202_traceability-md-classification.md)（1 回目）
- planning#354（キット docstring の番号衝突。**2 件目が戻ってこなかった当の是正**）
