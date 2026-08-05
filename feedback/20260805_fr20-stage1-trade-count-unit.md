---
title: Stage 1 の合格条件「最小取引件数 100 件」に計上単位の定義が無い（分割約定・手仕舞い注文の扱い）
type: plan-feedback
status: open
category: 要求の不足
related_ids: [FR-20, FR-12, ADR-0008]
source_repo: endazon/ai-stock-trading
source_ref: docs/adr/IADR-0149_stage1-trade-count-supply.md / docs/specs/20260805_386_stage1-trade-count.md / ブランチ feat/FR-20-386-stage1-trade-count
author: endazon (with Claude Code)
created: 2026-08-05
---

# フィードバック: Stage 1 の「最小取引件数 100 件」に計上単位の定義が無い

## 種別

要求の不足（確定した機械判定の条件が、判定の単位を述べていない）。

## 起点となる計画書

- 機能要求（FR）: FR-20（段階ゲート）。関連: FR-12（内蔵 `paper` との区別）
- 関連 ADR: ADR-0008（段階ゲート）
- 計画書リンク: `planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md`
  §4.1 条件3「最小取引件数 100 件」／ §4.3「最小取引件数（100 件）に届かない場合の扱い」／
  `06_technical/05_trading-assumptions.md` §5「1 日あたりの発注金額上限」

## 現状（計画書の記述 / As-Is）

§4.1 条件3 は次のとおり確定している。

| # | 条件 | 種別 | 定義 |
| --- | --- | --- | --- |
| 3 | **最小取引件数 100 件** | 機械判定 | 「運用に足るかを統計的に判断できる」最小サンプル数。100 件未満は分散が大きく、勝率の推定が判断材料にならない |

**しかし「1 件」が何を指すかを述べていない。** 同じ §4.1 の条件1（統制違反）は
「**計上単位は 1 回の発注拒否につき 1 件**（1 回の拒否で複数理由が返っても 1 件）」と単位を明記しているが、
条件3 には対応する記述が無い。判定に必須の定義でありながら、次の 3 点が未確定である。

1. **分割約定**: 1 注文が複数回に分かれて約定した場合、何件か
2. **手仕舞い（決済）注文**: 新規建てと手仕舞いをそれぞれ数えるのか、新規建てだけか
3. **未約定で終わった注文**: 発注はしたが約定しなかった注文を数えるか

## 問題点 / あるべき姿（To-Be）

- **単位の解釈だけで件数が 2〜3 倍変わる。** ブローカ（moomoo）は 1 注文について
  `Accepted`（約定 0）→ 部分約定 → 全量約定と非同期に状態を返し、約定数量は**累積値**である。
  約定イベントを数えると 1 注文が 2〜3 件になる。手仕舞いを別件として数えれば、さらに約 2 倍になる。
  **膨らむ側は「合格に早く届く」＝統制として緩い側**であり、しかも成功したように見える。
  100 件という値の根拠（統計的な最低ライン）を §4.3 が詳細に論じている以上、
  その 100 件が何の 100 件かは同じ精度で定まっている必要がある。
- **計画内に単位を推し量れる記述はあるが、条件3 自身は参照していない。**
  - `05_trading-assumptions` §5「1 日あたりの発注金額上限」は**新規建ての発注代金の合計**で判定し
    手仕舞いを算入しないと定めたうえで、「個人デイトレーダーの**取引件数**は 1 日 3〜5 件が一般的であり、
    1 注文上限いっぱいでも **6 件（＝2 回転）**まで収まる」と書く。ここでの「件」は新規建ての件数である。
  - §4.3 は同じ出典（1 日 3〜5 件）を用いて「100 件 ÷ 60 営業日 ＝ 1 営業日あたり約 1.7 件」と比較する。
    比較が成立するには 100 件も同じ単位でなければならない。
  - 条件3 の目的（勝率・平均損益の推定）にとっての標本は往復 1 回であり、新規建て 1 件と 1 対 1 に対応する。
- あるべき姿は、条件1 と同じ粒度で**条件3 にも計上単位を明記すること**である。例:
  > 計上単位は**約定が成立した新規建て注文 1 件**。1 注文が分割して約定しても 1 件、
  > 手仕舞い（決済）注文は計上しない。約定しなかった注文は計上しない。

## 実装側の暫定対応

実装は上記の読み取りを前提として採り、根拠を IADR-0149 決定2 に記録した。

```csharp
Stage1FillObservation(Guid DecisionId, DateOnly SessionDateEasternTime,
                      BrokerProvider Provider, PositionEffect PositionEffect)

// 1 件 ＝ 算入対象の発注先（moomoo SIMULATE）で約定が成立した新規建て注文 1 件。
// DecisionId で一意（分割約定・イベント再送でも 1 件）。
CountTrades(fills) = fills.Where(CountsAsTrade).Select(f => f.DecisionId).Distinct().Count()
```

**迷った箇所では少なく数える側（＝昇格が遅れる側）を選んだ。** 件数の水増しは
「実力が無いまま実弾へ進む」という不可逆な失敗に直結するためである。

計画側が「手仕舞いも数える」と裁定した場合、本実装の件数は約半分に見積もられており、
**昇格が遅れる方向**へ外れる（安全側だが計画とは食い違う）。裁定が確定し次第、
`Stage1Aggregation.CountsAsTrade` の 1 行を変えるだけで追随できる形にしてある。

## 影響範囲

- 影響する実装: `Stage1FillObservation` / `Stage1Aggregation.CountsAsTrade` / `CountTrades`（Risk.Domain）、
  `OrderExecutedStage1FillHandler`（Risk.Infrastructure）、`stage1_fill_observations` テーブル。
- 影響する仕様書: `docs/adr/IADR-0149_stage1-trade-count-supply.md`・
  `docs/specs/20260805_386_stage1-trade-count.md`・`docs/tests/FR-20_staged-gates-tests.md`。

## 送付

未送付。計画リポジトリ（endazon/project-planning）へ issue として起票する。
