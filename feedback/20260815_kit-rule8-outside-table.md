---
title: キットの traceability.md で「規則 8」が表の外に落ちている（GFM が表として描画しない）
type: plan-feedback
status: open
category: 誤り
related_ids: [NFR, IADR-0202]
source_repo: ai-stock-trading
source_ref: docs/specs/20260815_517_kit-sync-and-exclusion-removal.md（#517）
author: endazon (with Claude Code)
created: 2026-08-15
dispatched: true
planning_issue: 358
---

# 「規則 8」が表の外に落ちている

## 事実

`repo-template/.claude/rules/traceability.md`「是正・追随の母集合の取り方」の表で、
**規則 7 の行と規則 8 の行の間に空行がある**（`cat -A` で確認）。

```
| 7 | **数値・名前を 1 つ直したら…** | … |
                                          ← 空行
| 8 | **走査対象に自分の記録が入るときは…** | … |
```

**GFM はヘッダ行を持たない表本体を表として描画しない。** 空行で表が終わるため、
**規則 8 だけが素の文字列 `| 8 | … |` として表示される**（規則 1〜7 は表のまま）。

## 影響

- **内容は届いている**（テキストとしては読める）ため、**実害は表示だけ**である
- ただし**毎セッション必読の規約**であり、**1 行だけ体裁が違うと「後から雑に足した注記」に見える**。
  規則 8 は planning#350 の環流を受けた**正式な規則**である
- 規則 1〜7 は「破れたときに起きること」の列を持つが、**規則 8 は表の外にあるため列の対応が失われる**

## 依頼

規則 7 と規則 8 の間の空行を削除する（1 行）。

## 手元で直さない理由

`.claude/rules/traceability.md` は**キット配布物**である。本リポジトリでは 2026-08-15 に
**分類 C → A（バイト一致を機械検査する）へ移した**（[IADR-0202](../docs/adr/IADR-0202_traceability-md-classification.md)）。
手元で直すと `check-kit-sync.js` が赤くなる。

> **本 PR では取り込みを優先した。** 表示は壊れているが内容は届いており、
> **取り込まないほうが害が大きい**（規則 8 そのものが本リポへ来ない）。

## トリアージ結果

（計画側で記入）

## 関連

- [#517](https://github.com/endazon/ai-stock-trading/issues/517) / [IADR-0202](../docs/adr/IADR-0202_traceability-md-classification.md)
- planning#350（規則 8 の環流元）
- planning#349 / planning#354（キット配布物が自身の規約・前提と食い違う既報 2 件）
