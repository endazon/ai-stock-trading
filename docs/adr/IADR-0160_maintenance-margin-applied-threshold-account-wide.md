---
title: IADR-0160 維持率の適用閾値は口座単位（建玉ごとの閾値の最大値）とし、算式と条文選択を単一情報源へ寄せる
type: impl-adr
status: Accepted
related_ids: [FR-10, UC-06, ADR-0016, IADR-0130, IADR-0131, IADR-0132, IADR-0133, IADR-0154, IADR-0159]
author: Claude Code (implementation session)
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0160: 維持率の適用閾値は口座単位（建玉ごとの閾値の最大値）とし、算式と条文選択を単一情報源へ寄せる

- 状態: Accepted
- 日付: 2026-08-07
- 決定者: Claude Code（実装セッション。計画側の裁定は ADR-0016 決定7 の 2026-08-07 追記）

## 起点・関連

- 関連する計画書 ID: **FR-10**（空売り統制 (4) 維持率・「同一の注文に複数の上限が掛かる場合は常に厳しい方が効く」）／
  **UC-06**（維持率割れによる建玉の自動縮小）／**ADR-0016 決定7（2026-08-07 追記）**
- 関連する実装仕様書: [作業仕様書 20260807_420](../specs/20260807_420_maintenance-margin-threshold-account-wide.md)／
  [機能仕様書 FR-10](../functional/FR-10_risk-controls.md)／[テスト仕様書 FR-10](../tests/FR-10_risk-controls-tests.md)
- 隣接する実装 ADR: [IADR-0133](IADR-0133_maintenance-margin-auto-reduce.md)（自動縮小。決定2 が先に「最も厳しい建玉のもの」へ到達していた）／
  [IADR-0131](IADR-0131_short-selling-controls-fail-closed.md)（空売り統制のフェイルクローズ。決定4）／
  [IADR-0132](IADR-0132_product-type-tri-state-and-guard-scope.md)（商品種別 3 値）／
  [IADR-0130](IADR-0130_equity-ratio-risk-limits.md)（equity 比の保持と解決点の集約）／
  [IADR-0154](IADR-0154_supply-availability-declared-by-server.md)（供給の有無を値で偽装しない）
- 起点 issue: [#420](https://github.com/endazon/ai-stock-trading/issues/420)
- 環流: [feedback/20260804_uc06-maintenance-ratio-formula.md](../../feedback/20260804_uc06-maintenance-ratio-formula.md)（本追記の起点）／
  [feedback/20260807_adr0016-margin-long-maintenance-threshold.md](../../feedback/20260807_adr0016-margin-long-maintenance-threshold.md)（本 ADR が新たに起こしたもの）

## コンテキストと課題

計画 ADR-0016 決定7 は当初「閾値をいくつにするか」だけを定めており、**維持率が何を何で割った値か・建玉が
複数あるときどの閾値を使うか・信用買い側の規制値**を述べていなかった。実装（[IADR-0133](IADR-0133_maintenance-margin-auto-reduce.md)
決定1・決定2）は安全側で仮置きし環流した（project-planning#214）。2026-08-07 に裁定が下りた。

| 事項 | 確定値 |
| --- | --- |
| 維持率の算式 | **純資産 ÷ 建玉評価額の合計** |
| 建玉が複数あるときの適用閾値 | **建玉ごとの閾値の最大値**（最も厳しいもの） |
| 信用買い（マージンロング）の規制維持率 | **時価の 25%**（FINRA Rule 4210(c)(1)） |

### 実装側の欠陥

**適用閾値の求め方が 2 か所にあり、発注側（積み増しを止める側）だけが緩かった。**

| 箇所 | 適用閾値 |
| --- | --- |
| `MaintenanceMarginReducer.Plan`（自動縮小） | `Positions.Max(p => MaintenanceMarginThresholdFor(p.PriceUsd))` ＝ 裁定と一致 |
| **`ShortSellEvaluator`（新規建ての可否）** | **`MaintenanceMarginThresholdFor(intent.Price)` ＝ これから出す注文の株価だけ** |

規制側の実効維持率 `max($5.00 ÷ 株価, 30%)` は**低位株ほど厳しい**。$6.00 の空売り建玉は 83.3% を要求するが、
$50.00 の新規空売りを評価すると閾値は 40% に落ち、**口座の実維持率 50% でも通った**。自動縮小は 83.3% で
発動しているため、**縮小が走っている最中に評価器が積み増しを許す**。統制として自己矛盾している。

**これは「値の誤り」ではなく「同じ規則を 2 か所に書いた」ことの帰結である。** 片方だけを直しても、
次に規則が変わったときに同じ食い違いが再発する。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | `ShortSellEvaluator` 側に `Positions.Max(...)` を**書き足す** | **棄却**。同じ式が 3 か所になる。今回の欠陥の再発を止めない |
| B | 閾値の算出を新しい規則型（`MaintenanceMarginPolicy`）へ寄せ、両者がそこだけを通す | **採用**（決定1） |
| C | `MaintenanceMarginReducer` に評価器から相乗りする | 棄却。縮小は「計画を組む」型であり、可否判定の入力（注文意図）を持たない |

`ShortSellEvaluator` に建玉の情報をどう渡すかは別の選択である。

| 案 | 内容 | 評価 |
| --- | --- | --- |
| D-1 | `ShortSellOrderContext` に `MaintenanceMarginRatio`（現状）＋ `MarginPositions` を**並べて**持つ | 棄却。維持率と建玉が**別々に供給され得る**＝両者が食い違う状態を型が許す |
| D-2 | `MaintenanceMarginRatio` を `MaintenanceMarginSnapshot?` へ**置き換える** | **採用**（決定3） |
| D-3 | 建玉が供給されないときは注文の株価だけで判定を続ける | **棄却**。それは現状（欠陥）そのものである |

## 決定

### 決定 1: 適用閾値・回復目標・維持率の算式は `MaintenanceMarginPolicy` だけが定義する

```csharp
decimal? Ratio(decimal netEquityUsd, decimal totalMarketValueUsd)           // 純資産 ÷ 建玉評価額の合計
decimal  RegulatoryFor(decimal priceUsd, ProductType)                       // 条文の選択
decimal  ThresholdFor(ShortSellingLimits, decimal priceUsd, ProductType)    // 自前 40% と規制要求の厳しい方
decimal  AppliedThreshold(ShortSellingLimits, positions, newEntryPriceUsd?, ProductType)  // 候補の最大値
decimal  AppliedRecoveryTarget(...)                                         // 適用閾値 + 5pt
```

`MaintenanceMarginReducer` と `ShortSellEvaluator` は**自前で `Max` を書かない**。
`ShortSellingLimits.MaintenanceMarginThresholdFor(price)`（既存の公開 API・多数のテストが参照）も
`MaintenanceMarginPolicy.ThresholdFor(this, price, ShortSell)` へ委譲し、「厳しい方を採る」演算は
**プロダクトコード全体で 1 か所**に限る。

**候補が 1 つも無いときは例外**とする（既定値へ倒さない）。建玉も新規建ても無い口座に閾値は存在せず、
「0% の閾値」「40% の閾値」のいずれを返しても嘘になる。呼び出し側が先に短絡する
（`MaintenanceMarginReducer.Plan` は建玉ゼロで `null`）。

### 決定 2: 適用閾値の候補には**これから建てる建玉**を含める

新規注文は約定すれば建玉になる。含めなければ、「$50.00 の建玉しか無い口座で $6.00 の新規空売りを出す」
経路が素通りする（保有側 40% ＜ 新規側 83.3%）。**注文の向きに関わらず、厳しい方が効く**という
FR-10 の原則を、注文自身にも適用する。

> 計画（決定7 の追記）は「建玉ごとの閾値の最大値」と述べており、**新規注文をその候補に含めるかは明示していない**。
> 含める方が厳しい側であり FR-10 の原則に沿うため実装はそちらへ倒したが、追認を求めて環流する。

### 決定 3: `ShortSellOrderContext` は維持率（`decimal?`）ではなく**束**（`MaintenanceMarginSnapshot?`）を受ける

口座単位の閾値には保有建玉の**株価と商品種別**が要る。これは注文意図からも設定からも導けない外部入力である。

- **縮小側と同じ型**を受けるため、「同じ入力に対して同じ適用閾値」が構造的に保証される（テストでも固定した）。
- 維持率は同じ束から**導出**されるため（`NetEquityUsd ÷ TotalMarketValueUsd`）、算式が 2 か所で食い違わない。
- **`null` は「供給が無い」**という意味を保つ。**建玉 0 件・株価 0 の偽の束を作って埋めない**——
  それは「観測した結果ゼロだった」と読める値を発明することであり、[IADR-0154](IADR-0154_supply-availability-declared-by-server.md) /
  [IADR-0159](IADR-0159_buy-in-post-hoc-inference.md) 決定5 が禁じた形そのものである。
- 供給経路は依然として存在しない（#331 / #342）。本 ADR は**判定側の契約**だけを確定する。

### 決定 4: 束が供給されない／信頼できない／維持率を導出できないときは **fail-closed**（通さない）

| 状況 | 振る舞い |
| --- | --- |
| 束が `null`（供給なし）× 空売り建玉あり | **拒否**（`MaintenanceMarginBreach`）。[IADR-0131](IADR-0131_short-selling-controls-fail-closed.md) 決定4 の維持 |
| 束が `null` × 空売り建玉なし | 対象外（維持率という概念が成立しない） |
| 束はあるが `IsTrustworthy == false` × 空売り建玉あり | **拒否** |
| 束はあるが建玉 0 件（維持率を導出できない）× 空売り建玉あり | **拒否**（申告と束が不整合） |

**`IsTrustworthy` の検査を「維持率が導出できるか」で代用してはならない。** 必要証拠金が負の建玉は
**評価額としては成立してしまう**ため、維持率 90% が導出できてしまう。壊れた分母は維持率を実際より
良く見せる（[IADR-0133](IADR-0133_maintenance-margin-auto-reduce.md) 決定8）。また株価が負の建玉では
閾値の算出が**例外を投げる**ため、検査を外すと評価ループごと落ちて統制が実質オフラインになる。

**縮小側と評価側で安全側の向きは逆である**（縮小＝動かさない／評価＝通さない）。動かす統制の誤作動は
不可逆だが、積み増しを止めることは可逆であるためであり、この非対称は既に確定している。

### 決定 5: 規制維持率は**商品種別で条文を選ぶ**（信用買い＝25%・4210(c)(1)）

| 商品種別 | 条文 | 規制側の実効維持率 |
| --- | --- | --- |
| 空売り（`ShortSell`） | 4210(c)(3) | `max($5.00 ÷ 株価, 30%)` |
| 信用買い（`MarginLong`） | **4210(c)(1)** | **25%**（株価に依存しない） |
| 現物（`Cash`） | — | 維持率の対象外（供給側が除く）。万一渡されたら**空売り側の式へ倒す**（緩む側へ倒さない） |

$5.00 未満の空売り（4210(c)(2)）は**実装しない**——決定7 が空売りの対象から $5.00 未満を外しており発火しない。
発火しない分岐を書くと、テストで確かめられない規則が統制の中に残る。

**実効値は自前の 40% が常に上回るため、25% それ自体で判定が変わることはない。** それでも定数として持つのは、
代用のままでは**自前の 40% を将来見直したときに誤った規制下限が残る**ためである（自前を 20% へ下げると、
$6.00 の**信用買い**に条文に無い 83.3% が効く）。

## 帰結

### 良い影響

- **積み増し側と縮小側の閾値が一致する。** 「縮小の最中に積み増しを許す」自己矛盾が構造的に起こらない。
- 適用閾値・回復目標・算式・条文選択の 4 つが、それぞれ 1 か所にしかない。
- 維持率の定義（純資産 ÷ 建玉評価額の合計）が供給開始前に固定された。供給元が別定義の値を返しても
  判定は本算式を通る。

### 悪い影響・残余リスク

- **信用買いの適用閾値が緩む方向へ変わる。** 株価 $12.50 未満の信用買い建玉について、従前は空売り側の式
  （$6.00 なら 83.3%）を代用していたものが **40%** になる。条文どおりの是正であり計画の指示に沿うが、
  **計画の追記は「実効値は変わらない」と述べており、代用していた実装にとっては変わる**。環流した。
- **本統制は依然として発火しない。** 束の供給経路が無いため、実際の口座では常に fail-closed 側
  （空売り建玉があれば拒否）に落ちる。空売り自体が借株照会の不在で全件拒否されている現況では
  観測もされない（`docs/blocked-tasks.md`）。
- **閾値ちょうどの維持率では、縮小が発動しながら新規建てが通る。** 縮小の発動条件は `維持率 ≦ 閾値`
  （[IADR-0133](IADR-0133_maintenance-margin-auto-reduce.md) 決定3。「割り込む前に動く」）だが、
  規則 (4) の拒否条件は `維持率 < 閾値`（「割り込む」）である。**本 issue は閾値の値の食い違いを直すもので
  あり、この等号の非対称は範囲外**として変更しなかった（どちらも計画由来であり、片方を動かすと既存の
  境界テストが定めた解釈を実装判断で覆すことになる）。環流して裁定を仰ぐ。
- `MaintenanceMarginSnapshot` を判定コアの入力に据えたことで、将来の供給実装は**純資産と建玉の束**を
  組み立てる必要がある（維持率だけを返す API では足りない）。これは口座単位の閾値を求める以上避けられない。

## 追跡

- テスト: `MaintenanceMarginAppliedThresholdTests`（T-10-248〜256）・`MaintenanceMarginAutoReduceTests`・
  `ShortSellingControlsTests`
- ミューテーションテスト: 適用閾値を注文の株価だけへ戻す／最大値を最小値・平均へ替える／信用買いの 25% を
  空売り側の式へ戻す／`IsTrustworthy` の検査を外す —— **4 件すべてで赤くなることを確認した**
