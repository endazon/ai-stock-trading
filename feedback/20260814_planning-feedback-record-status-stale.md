---
title: 計画リポの環流記録 `20260708_trading-defaults-derived-values.md` が `status: open` のままで、対応する planning#61 の決着に追随していない
type: plan-feedback
status: open
category: その他
related_ids: [NFR, FR-10, FR-17, FR-19]
source_repo: endazon/ai-stock-trading
source_ref: docs/specs/20260814_477_feedback-vocabulary-kit-sync.md / IADR-0188 / issue #477
author: endazon (with Claude Code)
created: 2026-08-14
dispatched: true
planning_issue: 329
---

# フィードバック: 計画側の環流記録が issue の決着に追随していない（1 件）

## 種別

**その他**（計画リポジトリ内の記録の陳腐化。計画書の誤りではない）。

## 起点となる計画書

計画リポジトリ `draft/feedback/20260708_trading-defaults-derived-values.md`
（原典は本リポ `feedback/20260708_trading-defaults-derived-values.md`）。
対応する計画側 issue: [project-planning#61](https://github.com/endazon/project-planning/issues/61)
「計画feedback(FR-10, FR-19, §5): 金額系の統制上限3値が §5 既定値表に無く、実装が備考から逆算している」。

## 現状（As-Is）

**#477 / IADR-0188 の全数実測（2026-08-14・計画 pin `cff0e7b`）で見つかった。**

本リポの環流記録 30 件を計画側と突合したところ、**29 件は計画側の `status` が実装側と整合していた**が、
本件だけが**逆向き**であった。

| | 実装側 | 計画側の記録 | 計画側 issue |
| --- | --- | --- | --- |
| `20260708_trading-defaults-derived-values.md` | `accepted`（本作業で転記） | **`status: open`** | **planning#61 は CLOSED** |

計画側の `draft/feedback/20260708_trading-defaults-derived-values.md` は
**トリアージ結果の節を持たない実装側記録の逐語コピー**であり、**受理された事実がどこにも書かれていない**。

一方、実装側の記録は「1 注文あたりの発注金額上限 ＝ equity の 25%」の確定日を
**2026-08-02（planning#61）**として引いており、**決着そのものは起きている**。

## 問題点 / あるべき姿（To-Be）

**計画側の記録だけを読むと「まだ裁定されていない」と読める。**

これは実装側で今回是正したのと**同型の陳腐化**である（実装側は 18 件が `open` のまま、
計画側では `accepted` になっていた）。**向きが逆なだけで、原因は同じ ——
`status` が人手更新であり、issue の決着が記録へ自動では伝わらない。**

**あるべき姿**: 計画側の記録 `status` を `accepted` へ改め、planning#61 の決着（2026-08-02）を追記する。

## 提案（計画への反映案）

- 反映先候補: 計画リポ `draft/feedback/20260708_trading-defaults-derived-values.md`
- 提案内容:
  1. `status: open` → **`accepted`**
  2. トリアージ結果の節を追記し、**planning#61 の決着（2026-08-02）と反映先**（§5 既定値表）を明記する
  3. 併せて、**計画側の他の記録にも同型の陳腐化が無いか**を棚卸しする
     （本実測では本件のみ検出したが、走査は本リポの環流記録 30 件と同名のファイルに限っている ——
     **計画側にしか存在しない記録は見ていない**）

## 影響範囲

- **記録の可読性のみ。** 統制・実装・計画書の本文には影響しない
  （planning#61 の決着は既に計画 §5 へ反映済みであり、実装の `TradingDefaults` も追随済み）。
- ただし**週次の棚卸し・定期突合が本記録を「裁定待ち」として拾い続ける**ため、
  放置すると恒常的な騒音になる（本リポで実際に起きた形。IADR-0188 参照）。
