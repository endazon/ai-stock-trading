---
title: PlanRiskDefaults の全項目を計画書の現行値と再照合する（型の形しか見ていない 11 値を実値検査へ引き上げる）
type: spec
status: review
related_ids: [FR-10, FR-17, FR-19, FR-20, ADR-0016, ADR-0018, ADR-0021, ADR-0022, IADR-0172]
author: endazon (with Claude Code)
created: 2026-08-07
updated: 2026-08-07
---

# 仕様書: PlanRiskDefaults の再照合

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 一次情報: `planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md` §1・§3・§4・§5・§6・§6.1
- 計画 ADR: **ADR-0016**（空売り統制）・**ADR-0018**（リスク既定値の同期）・**ADR-0008**（段階ゲート）・**ADR-0022**（為替）・**ADR-0021**（米国口座種別）
- 起点 issue: [#445](https://github.com/endazon/ai-stock-trading/issues/445)
- 実装 ADR: **[IADR-0172](../adr/IADR-0172_plan-risk-defaults-value-level-conformance.md)（本作業で新設）**／既存: [IADR-0166](../adr/IADR-0166_plan-source-digest.md)（本 issue の起票元）・[IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md)

## 目的・背景

**#445 は「1 件の誤転記の後始末」として起票された。実際に見つかったのは、それより重い構造的な穴であった。**

起票の経緯: [IADR-0166](../adr/IADR-0166_plan-source-digest.md) が計画書 → `PlanRiskDefaults` の**人手転記**を検知する仕組みを入れたが、**その仕組みが導入された時点の値そのものは検査されていなかった**（ベースラインは「現在の計画書」で取るため、既に転記を誤っていれば誤りごと固定される）。実際に `Fx.StaleRateWarningDays` が **3 日のまま**で、計画は 2026-08-07 に **5 日**へ改訂済みであった（PR #444 で修正）。

**残り 33 項目にも同じ誤りが潜んでいないか。それが #445 の問いである。**

## 調査結果（実測・2026-08-07）

### 結論1: **値の誤転記は 1 件も残っていなかった**

`PlanRiskDefaults` の全 34 項目を計画書の現行版（submodule `a4616a8`）と 1 件ずつ突き合わせた。**#444 で直した `Fx.StaleRateWarningDays` 以外に、値の誤りは無かった。**

これは重要な結論であり、そのまま記録する —— **「全部見たが問題は無かった」は、見ていないことと区別して書かれなければならない。**

### 結論2: 🔴 **`ShortSell.Limits` は型の形しか見ていない**

**計画が確定した 7 つの数値が、実際には 1 つも比較されていなかった。**

抽出側（`ActualDefaults`）は `DescribeTypeWithMembers` を使っており、返すのは**プロパティ名の一覧だけ**である。

```csharp
var members = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);
return $"type {typeName} with members: {Sorted(members)}";   // ← 値は一度も読まれない
```

したがって `TradingDefaults.CreateShortSellSettings()` の `PerSymbolCapRatio` を **0.10 → 0.50** に変えても、`PlanConformanceTests` も `PlanSourceDigestTests` も**緑のまま**である。

| 未検査だった値 | 実装値 | 計画の確定値 | 出典 |
| --- | --- | --- | --- |
| `PerSymbolCapRatio` | 0.10 | equity の 10% | §5 / ADR-0016 決定2 |
| `BorrowRateCapAnnual` | 0.20 | 年率 20% | §5 / ADR-0016 決定3 |
| `MaintenanceMarginThreshold` | 0.40 | 40% | §5 / ADR-0016 決定7 |
| `MaintenanceRecoveryTargetOffset` | 0.05 | +5 ポイント | §5 |
| `PriceFloorUsd` | 5.00 | $5.00 | §5 / ADR-0016 決定7 |
| `ExposureRatioCap` | 0.50 | 建玉総額の 50% | §5 / ADR-0016 決定9 |
| `BuyInBanDurationDays` | 30 | 30 日 | ADR-0016 決定4 |

**7 値とも現在は計画どおりである。**「間違っていた」のではなく「**間違っても気付けない**」——[IADR-0166](../adr/IADR-0166_plan-source-digest.md) が名付けた「緑だが検査されていない」そのものである。

### 結論3: 🟡 **計画が確定した 4 値が表に無い**（いずれも実装済み）

| 値 | 実装 | 計画 | 備考 |
| --- | --- | --- | --- |
| 規制側の維持率下限 **30%** | `ShortSellingLimits.RegulatoryMaintenanceMarginFloor` | §5（`max($5.00 ÷ 株価, 30%)`） | |
| 規制側の固定額 **$5.00/株** | `RegulatoryFixedMaintenancePerShareUsd` | 同上 | |
| 信用買いの規制維持率 **時価の 25%** | `RegulatoryMarginLongMaintenanceMargin` | §5（FINRA 4210(c)(1)） | **2026-08-07 確定**（質問票 第 13 回 Q4-3）。**確定から本作業まで一度も検査されていない** |
| 空売り実弾解禁の自己資金 **$5,000** | `StageProductPolicy.ShortSellLiveReleaseEquityUsd` | §5 / ADR-0016 決定8 | |

### 結論4: 🟡 **`Guard.PreventSameDayReentry` の出典注記が古い**

表の注記は「**適用範囲は日本株現物**」だが、計画 §5 は 2026-08-06 に `現物 && （日本市場 ‖ 現金口座）` へ改められている（ADR-0021 決定4-1・環流 project-planning#220）。**値（`True`）は正しく、注記だけが計画より古い。**

注記は飾りではない —— **読み手が「この値は本当に計画どおりか」を確かめる唯一の手掛かり**である。古い注記は、確かめた気にさせる。

> 実装側の適用範囲そのものが計画に追随しているかは **#380 の担当**であり、本作業では注記のみ直す（値の判定ロジックには触れない）。

## 対象範囲

### 対象

| # | 変更 | 内容 |
| --- | --- | --- |
| 1 | `PlanRiskDefaults` へ **11 行**追加 | 空売り 7 値 ＋ 規制 3 値 ＋ 解禁資金 1 値（34 → **45 項目**） |
| 2 | `ActualDefaults` へ 11 件の抽出を追加 | すべて**実装の定数・既定値から機械的に導出**する（手写ししない） |
| 3 | `Guard.PreventSameDayReentry` の注記を計画の現行文へ直す | |
| 4 | `ShortSell.Limits`（型の形）は**残す** | 値検査とは役割が違う（後述） |

### 対象外（意図的にやらない）

- **`ShortSell.Limits`（型の形）の削除**。メンバの**増減**を捕まえるのは依然この行だけである（値の行は「そのメンバがある」ことを前提にしている）。
- **実装値の変更**。本作業で見つかった逸脱は 0 件であり、直すものが無い。
- **`Guard.PreventSameDayReentry` の判定ロジック**（→ #380）。
- **スリッページ 0.1%（§4）・配当課税 20.315%・米国株配当源泉 10%（§1）の収録**。§4 のスリッページは「初期値（案）・実測で見直す」であり確定値ではなく、**実装側にも既定の定数が存在しない**（`BacktestCostModel` の構築引数）。配当課税・米国源泉は**実装がまだ持っていない**。**表に入れると `ActualDefaults` に書き写す以外に手が無く、「紙の上の一致」になる**（`ActualDefaults` 冒頭が禁じている形）。
- **`Guard.EnabledProductTypes = "Cash"` の再検討**。計画 §5 は段階別の可否を定めるが「設定の既定値」を名指ししていない。既存行の妥当性の議論であり、本作業の照合結果に変更を要する誤りは無かった。

## 実装上の判断（IADR-0172 に記録する）

| # | 判断 | 内容 |
| --- | --- | --- |
| 1 | **型の形の検査と値の検査は併存させる** | 役割が違う。前者はメンバの増減、後者は値の逸脱を捕まえる |
| 2 | **`ShortSell.PriceFloorUsd` と `Regulatory.FixedMaintenancePerShare` を別の行として持つ** | **どちらも $5.00 だが別の概念**である（空売り対象の株価下限／規制の固定維持証拠金）。1 行にまとめると、片方が変わったときにもう片方の検査が黙って消える |
| 3 | **値は単位つきで正規化する** | `equity ratio 0.10` / `USD 5.00` / `30 days`。無次元の数値どうしの取り違えを防ぐ（既存の `Fx.*` と同じ作法） |
| 4 | **抽出はすべて実装から機械的に導出する** | 手写しすると `PlanRiskDefaults` と同じ紙の上の一致になり、既知逸脱の陳腐化検知（IADR-0127 検査3）が働かなくなる |

## 受け入れ基準

- [ ] `PlanRiskDefaults` が 45 項目になり、追加 11 値がすべて計画の確定値と一致する
- [ ] `ActualDefaults` の追加抽出が**すべて実装の定数・既定値から導出**されている（リテラルの手写しが無い）
- [ ] `Guard.PreventSameDayReentry` の注記が計画の現行文と一致する
- [ ] **ミューテーション**: 追加した各値を実装側で 1 つずらすとテストが赤くなる（**本作業の主目的そのもの**）
- [ ] **ミューテーション（対照）**: 同じ変異が**変更前の実装では緑のまま**であることを実測する
- [ ] `dotnet build` / `dotnet test` が通る

## テスト方針

**本作業は「検査を増やす」作業であるため、増やした検査が効くことの実測がすべてである。**

とくに重要なのは**変更前後の対比**である —— 「変異させたら赤くなった」だけでは、**その検査が本作業で増えたものなのか元からあったのか**が分からない。`PerSymbolCapRatio` を 0.10 → 0.50 に変える同じ変異が、**変更前は緑・変更後は赤**であることを両方実測する。

## 残余リスク

1. **本表は「実装が持っている値」しか検査できない。** 計画が確定していても実装に無い値（配当課税・米国源泉）は表に入れられず、**実装されるまで無検査のまま**である。`PlanSourceDigestTests` は計画側の変更を検知するが、「計画にあって実装に無い」ことは指摘しない。
2. **`ShortSell.Limits` の型の形の検査は、メンバが増えたときに落ちる。** 増やした本人が値の行も足すとは限らず、**メンバだけ増えて値が無検査**という状態は作れる（型の行を直せば緑になるため）。
3. **注記（`Citation`）は機械検査されない。** 今回直した `Guard.PreventSameDayReentry` と同じ陳腐化は他の行でも起こり得る。`PlanSourceDigestTests` は計画本文の変更を捉えるため、**変更に気付く経路はある**が、注記を直すかどうかは人の判断に残る。
4. **本作業は 2026-08-07 時点の計画（submodule `a4616a8`）に対する照合である。** 計画が動けば再照合が要る —— それを自動で気付かせるのが `PlanSourceDigestTests` の役割であり、本作業はその前提となるベースラインを正しくする作業である。
