---
title: 作業仕様書 — 維持率の適用閾値を口座単位（建玉ごとの閾値の最大値）へ揃え、算式と信用買いの規制値を確定させる（ADR-0016 決定7 の 2026-08-07 追記）
type: work
status: review
related_ids: [FR-10, UC-06, ADR-0016, IADR-0130, IADR-0131, IADR-0132, IADR-0133, IADR-0160]
author: endazon (with Claude Code)
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
related_specs:
  - ../adr/IADR-0160_maintenance-margin-applied-threshold-account-wide.md
  - ../adr/IADR-0133_maintenance-margin-auto-reduce.md
  - ../adr/IADR-0131_short-selling-controls-fail-closed.md
  - ../adr/IADR-0132_product-type-tri-state-and-guard-scope.md
  - ../adr/IADR-0130_equity-ratio-risk-limits.md
  - ../functional/FR-10_risk-controls.md
  - ../tests/FR-10_risk-controls-tests.md
  - ../blocked-tasks.md
  - ../DEFINITION_OF_DONE.md
---

# 作業仕様書: 維持率の適用閾値を口座単位へ揃える（#420）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-10**（空売り専用統制 (4) 維持率／「同一の注文に複数の上限が掛かる場合は常に厳しい方が効く」）
- ユースケース（UC）: **UC-06**（維持率割れによる建玉の自動縮小）
- 関連 ADR: **ADR-0016 決定7（2026-08-07 追記＝維持率の算式・複数建玉時の適用閾値・信用買いの規制維持率）**／
  ADR-0016 決定10（拒否理由 9 種）／ ADR-0016 決定14（Stage 1 で検証できない統制）
- 実装 ADR: **[IADR-0160](../adr/IADR-0160_maintenance-margin-applied-threshold-account-wide.md)（本作業）**／
  [IADR-0133](../adr/IADR-0133_maintenance-margin-auto-reduce.md)（自動縮小の決定的規則・決定2 が先に「最も厳しい建玉のもの」へ到達していた）／
  [IADR-0131](../adr/IADR-0131_short-selling-controls-fail-closed.md)（空売り統制のフェイルクローズ。決定4）／
  [IADR-0132](../adr/IADR-0132_product-type-tri-state-and-guard-scope.md)（商品種別 3 値）
- 起点 issue: [#420](https://github.com/endazon/ai-stock-trading/issues/420)
- 計画 submodule: **`06fa163`**（本作業では更新しない。取り込む追記は既に pin 済み）
- 環流: [feedback/20260804_uc06-maintenance-ratio-formula.md](../../feedback/20260804_uc06-maintenance-ratio-formula.md)
  （本追記はこの環流＝project-planning#214 への裁定である）／
  **本作業で新たに [feedback/20260807_adr0016-margin-long-maintenance-threshold.md](../../feedback/20260807_adr0016-margin-long-maintenance-threshold.md) を起こす**

## 目的・背景

計画側が 2026-08-07 に ADR-0016 決定7 へ追記を入れ、実装が仮置きしていた 3 点に裁定が下りた。

| 事項 | 確定値 |
| --- | --- |
| 維持率の算式 | **純資産 ÷ 建玉評価額の合計** |
| 建玉が複数あるときの適用閾値 | **建玉ごとの閾値の最大値**（最も厳しいもの）を口座に適用する |
| 信用買い（マージンロング）の規制維持率 | **時価の 25%**（FINRA Rule 4210(c)(1)） |

### 実装側の欠陥（本作業の主眼）

**適用閾値の求め方が 2 か所で食い違っており、発注側（積み増しを止める側）が緩い。**

| 箇所 | 適用閾値 | 裁定との一致 |
| --- | --- | --- |
| `MaintenanceMarginReducer.Plan`（自動縮小） | `snapshot.Positions.Max(p => limits.MaintenanceMarginThresholdFor(p.PriceUsd))` | 一致 |
| **`ShortSellEvaluator`（新規建ての可否）** | **`limits.MaintenanceMarginThresholdFor(intent.Price)` ＝ これから出す注文の株価だけ** | **不一致** |

破れ方（issue 本文の例をそのままテストにする）:

- 既存の空売り建玉が **$6.00** → 規制維持率 `max($5.00, 0.30×6.00) ÷ 6.00 = 83.3%`。口座に要る閾値は **83.3%**。
- ここへ **$50.00** の新規空売りを出すと、評価器の閾値は `max(40%, 30%) = 40%` になる。
- **口座の実維持率 50% でも「40% を上回るから可」と通る。** 自動縮小は 83.3% で発動しているため、
  **縮小が走っている最中に評価器が積み増しを許す**という自己矛盾になる。

## 対象範囲（やること）

1. `ShortSellEvaluator` の適用閾値を**口座単位**（保有建玉ごとの閾値 ∪ **これから出す注文自身の閾値**の最大値）へ改める。
2. 適用閾値の算出を **1 か所へ寄せる** —— 新設する `MaintenanceMarginPolicy`（純粋な規則型）を単一情報源とし、
   `MaintenanceMarginReducer` と `ShortSellEvaluator` の双方がそこだけを通す。
3. **維持率の算式（純資産 ÷ 建玉評価額の合計）を定義として記録する。** 実装上も
   `MaintenanceMarginPolicy.Ratio` を唯一の算出点にし、`MaintenanceMarginSnapshot` はそれを通す。
4. **信用買い（マージンロング）の規制維持率 25%（4210(c)(1)）を定数として持ち**、商品種別で条文を選び分ける
   （空売り＝4210(c)(3)）。

## 対象外（やらないこと）

- 自前閾値 **40%**・回復目標オフセット **+5 ポイント**・株価下限 **$5.00** の**値の変更**（決定7 は値を改訂していない）
- **4210(c)(2)**（株価 $5.00 未満）の実装 —— 決定7 が空売りの対象から $5.00 未満を外しており**発火しない**
- **維持率の供給経路の実装**（供給元が無いことは決定7 の 2026-08-07 追記が「Stage 1 の全期間にわたって
  画面に表示できない」と確認済み。判定側だけを直す。#331 / #342 の担当）
- 拒否理由の追加・改名（`MaintenanceMarginBreach` のまま）

## 設計

### 単一情報源: `MaintenanceMarginPolicy`（新設・Domain・純関数）

```csharp
decimal? Ratio(decimal netEquityUsd, decimal totalMarketValueUsd)          // 純資産 ÷ 建玉評価額の合計
decimal  ThresholdFor(ShortSellingLimits, decimal priceUsd, ProductType)   // 自前 40% と規制要求の厳しい方
decimal  AppliedThreshold(ShortSellingLimits, IEnumerable<MarginPosition>, decimal? newEntryPriceUsd, ProductType)
decimal  AppliedRecoveryTarget(...)                                        // 適用閾値 + 5pt
```

- `AppliedThreshold` は「保有建玉ごとの閾値」と「（あれば）これから建てる建玉の閾値」を**候補にまとめて最大**を採る。
  最大を採る場所が 1 か所しか無いため、片方だけ緩い状態を**構造的に**作れない。
- 候補が 1 つも無いとき（建玉なし・新規建てなし）は**維持率という概念が成立しない**。呼び出し側が先に短絡する
  （`MaintenanceMarginReducer` は `Positions.Count == 0` で `null` を返す。既存の挙動）。

### 商品種別ごとの規制維持率（4210(c)）

| 商品種別 | 条文 | 規制側の実効維持率 |
| --- | --- | --- |
| 空売り（`ShortSell`） | **4210(c)(3)** | `max($5.00 ÷ 株価, 30%)` |
| 信用買い（`MarginLong`） | **4210(c)(1)** | **25%**（時価の 25% ÷ 時価） |
| 現物（`Cash`） | — | 供給側が維持率の対象から除く。万一渡された場合は**空売り側の式**へ倒す（緩む側へ倒さない） |

### `ShortSellEvaluator` の入力

適用閾値を口座単位で求めるには**保有建玉の株価と商品種別**が要るが、`ShortSellOrderContext` は
今それを持たない（維持率だけを `decimal?` で受けている）。**供給されない値を 0 や null で埋めて
「観測した結果ゼロだった」と読まれる値を発明しない**（IADR-0154 / IADR-0159 と同じ規律）ため、
`decimal? MaintenanceMarginRatio` を **`MaintenanceMarginSnapshot? MarginSnapshot`** へ置き換える。

- 縮小側（`MaintenanceMarginReducer`）と**同じ型**を受けるため、「同じ入力に対して同じ閾値」が構造的に保証される。
- 維持率は同じ束から**導出**される（`NetEquityUsd ÷ TotalMarketValueUsd`）ため、算式が食い違い得ない。
- `null` は**供給が無い**ことを意味する（従前の `MaintenanceMarginRatio == null` と同じ）。

#### 縮退（フェイルクローズの向き）

| 状況 | 振る舞い | 理由 |
| --- | --- | --- |
| `MarginSnapshot == null`（供給なし）× 空売り建玉あり | **拒否**（`MaintenanceMarginBreach`） | 既存。IADR-0131 決定4 |
| `MarginSnapshot == null` × 空売り建玉なし | 対象外（拒否しない） | 既存。維持率という概念が成立しない |
| **`MarginSnapshot` はあるが信頼できない**（`IsTrustworthy == false`）× 空売り建玉あり | **拒否** | 壊れた分母は維持率を実際より良く見せる。**評価器の安全側は「通さない」** |
| `MarginSnapshot` はあるが**建玉が 1 件も無い**（維持率が導出できない）× 空売り建玉あり | **拒否** | 申告（`TotalShortExposure > 0`）と束が不整合。確認できないまま積み増さない |
| 株価が下限 $5.00 未満（規則 5 で既に拒否） | 本判定を評価しない | 既存。規制式の分母として意味を持たない |

**縮小側の安全側は「動かない」、評価器の安全側は「通さない」**（向きが逆であることは IADR-0133 決定5 と
IADR-0131 決定4 が既に確定している）。本作業はその非対称を維持する。

### 既知の振る舞い変更（信用買いの規制値）

`MarginLong` の規制維持率が空売り側の式から **25%** へ変わるため、**株価 $12.50 未満の信用買い建玉**を
含む口座では適用閾値が下がる（例: $6.00 の信用買い ＝ 83.3% → **40%**）。
計画の追記は「実効値は変わらない（常に 40% が効く）」と述べているが、**実装は決定7 が定めていなかった
空売り側の式で代用していた**（IADR-0133 決定2 が明記のうえ環流していた）ため、追記の取り込みは
**信用買いに限って緩む方向の変更**になる。これは条文どおりの是正であり計画の指示に沿うが、
**緩む方向であることは計画側が認識していない可能性がある**ため環流する（`feedback/20260807_*`）。
空売り建玉の閾値は**一切変わらない**。

## 受け入れ基準

- [ ] 既存の空売り建玉 $6.00 を保有した状態で $50.00 の新規空売りを維持率 50% で評価すると**拒否**される
- [ ] 建玉が 1 件（＝注文と同じ株価）のときは従前と同じ結果になる（回帰）
- [ ] `MaintenanceMarginReducer` と `ShortSellEvaluator` が**同じ入力に対して同じ適用閾値**を出す
- [ ] 建玉が無いときは維持率の概念が成立せず対象外である（否定形・既存）
- [ ] 維持率が供給されない × 空売り建玉あり → fail-closed（既存・IADR-0131 決定4 の回帰）
- [ ] 適用閾値が**最大値**であり、最小値・平均へ退行しない（否定形）
- [ ] 信用買いの規制維持率が **25%** であり、空売り側の式へ退行しない
- [ ] 維持率の算式が `純資産 ÷ 建玉評価額の合計` の 1 か所だけで定義されている
- [ ] `dotnet build` 警告ゼロ・`dotnet test` 全緑・`dotnet format --verify-no-changes`

## テスト方針

テスト仕様書 [FR-10_risk-controls-tests](../tests/FR-10_risk-controls-tests.md) に
**T-10-248〜T-10-256** を追記する（3 点セット＝境界値・プロパティ・否定形）。
ミューテーションテスト（実装を意図的に壊してテストが赤くなることを確認）を 3 件行う。

1. `ShortSellEvaluator` の適用閾値を `intent.Price` だけに戻す
2. 適用閾値を最大値ではなく最小値／平均にする
3. 信用買いの 25% を空売り側の式に戻す

## 影響範囲

| 対象 | 変更 |
| --- | --- |
| `MaintenanceMarginPolicy`（新規） | 適用閾値・回復目標・維持率の算式の**単一情報源** |
| `ShortSellingLimits` | `RegulatoryMarginLongMaintenanceMargin = 0.25m` の追加・`RegulatoryMaintenanceMarginFor(price, productType)` の追加 |
| `ShortSellOrderContext` | `MaintenanceMarginRatio`（`decimal?`）→ `MarginSnapshot`（`MaintenanceMarginSnapshot?`） |
| `ShortSellEvaluator` | (4) を口座単位の適用閾値へ |
| `MaintenanceMarginReducer` | 閾値・回復目標の算出を `MaintenanceMarginPolicy` へ委譲（値は不変） |
| `MaintenanceMarginSnapshot` | 維持率の算出を `MaintenanceMarginPolicy.Ratio` へ委譲 |
| テスト | `ShortSellingControlsTests` / `MaintenanceMarginAutoReduceTests` |
| 文書 | 本書・IADR-0160・機能仕様書・テスト仕様書・環流・`blocked-tasks.md` |
