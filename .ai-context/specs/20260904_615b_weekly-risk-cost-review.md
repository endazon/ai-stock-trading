---
title: 週報 §5 リスク・費用レビューを、費用の内訳と費用率で実体化する
type: spec
status: review
related_ids: [FR-06, FR-07, FR-16, FR-17, UC-05, ADR-0030, IADR-0025, IADR-0269, IADR-0291, IADR-0301]
author: endazon (with Claude Code)
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0030_report-section-numbering-is-plan-canonical.md
---

# 仕様書: 週報 §5 リスク・費用レビューを、費用の内訳と費用率で実体化する

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-06（報告サイクル）／FR-07（報告書の階層管理）／FR-16（報告書テンプレート）／FR-17（全体前提条件）
- ユースケース（UC）: UC-05（報告書の確定）
- 画面（SC）: なし
- 関連 ADR: 計画 ADR-0030（節番号・節順は計画が正・未実装の節を詰めない）
- 関連 IADR: [IADR-0301](../adr/IADR-0301_fill-level-pnl-attribution-single-fold.md)（約定単位の損益帰属＝内訳の単一情報源）／
  [IADR-0291](../adr/IADR-0291_report-sections-follow-plan-numbering.md)（未実装節の見出し出力）／
  [IADR-0269](../adr/IADR-0269_trade-history-wiring-and-record-based-supply.md)（未供給の規律・出口での結線固定）／
  [IADR-0025](../adr/IADR-0025_pnl-aggregation.md)（損益集計の純関数）
- 関連する作業仕様書: [20260904_615a_weekly-daily-progression-and-highlights](./20260904_615a_weekly-daily-progression-and-highlights.md)
- 計画書リンク: `project-planning/projects/ai-stock-trading/06_technical/04_report-templates.md` §週報テンプレート §5
  （隣接クローンで確認。GitHub 上の相当パス:
  `https://github.com/endazon/project-planning/blob/main/projects/ai-stock-trading/06_technical/04_report-templates.md`）

## 目的・背景

[#615](https://github.com/endazon/ai-stock-trading/issues/615) のスライス b。週報 §5 は IADR-0291 以来
「本節は未実装です（#615 で実装予定）」のままである。計画の節本文は次の 2 行である。

```
## 5. リスク・費用レビュー
- 損切り執行 <n 件>・発注拒否 <n 件>・上限使用率の週間最大 <n%>
- 費用の内訳（手数料/諸費用/税）と損益に対する費用率 <n%>
```

**#615 が対象としているのは 2 行目（費用の内訳・費用率）だけである。** 1 行目の 3 項目は
記録源がいずれも存在しない（後述）。

## 対象範囲

- 対象:
  - `CostCalculator` へ**内訳を返す純関数を非破壊で追加**する（既存の署名・呼び出し元は変えない）。
  - 期間の費用レビュー（内訳・費用率）を作る Domain 純関数を新設する。
  - `ReportView` へ入力フィールドを 1 つ追加し、`ReportDraftService` の週報経路で結線する。
  - `ReportRenderer` の週報 §5 を `AppendNotImplemented` から実体の描画へ置き換える。
  - ゴールデン 2 本（`Tests/Domain/Golden/weekly-{supplied,unsupplied}.md`）の更新と焦点テストの追加。
- 対象外:
  - **月報 §2 週別・市場別の内訳**（スライス c・別 PR）。本 PR は `AppendNotImplemented` を残す。
  - 月報 §3 税金レビュー・日報 §6 振り返り（別の理由定数。#615 の対象外）。
  - **月報 §1 の「費用合計 / 費用率」**。同じ分母定義が要るが、月報のゴールデンを動かすと本 PR の差分が
    「新設 1 節の中身だけ」でなくなる。**フォローアップとして IADR に残す。**
  - 新しい HTTP 端点・イベント・設定。**1 つも要らない**（入力は既存の約定列と前提条件だけ）。
  - 節番号の繰り上げ。**既存の §4・§6 は計画正のまま動かさない**（ADR-0030 決定1・決定2）。

## 記録源の実測（何が出せて何が出せないか）

| 計画の項目 | 記録源 | 本 PR での扱い |
| --- | --- | --- |
| 損切り執行 `<n 件>` | **無い。** 判断の起点（`DecisionTriggerKind`）は取引判断サービスのプロセス内にしかなく、`TradeDecisionMade` にも `OrderIntent` にも列が無い（`TradeHistoryViewBuilder` は `Trigger: null` を直書きしている） | 未供給と描く |
| 発注拒否 `<n 件>` | **無い。** 報告書サービスは拒否の記録源（`IPeriodFillSource` 相当のポート）を持たない。**約定列には拒否が現れない**（約定していないため） | 未供給と描く |
| 上限使用率の週間最大 `<n%>` | **無い。** #615 が明示的に対象外とした共通ギャップ（日報 §4 の同項目も未実装） | 未供給と描く |
| 手数料 | `CostCalculator` の `commission` 項 | **出す** |
| 諸費用 | **無い。** 計画 `05_trading-assumptions` §2 の「米国株 売却時諸費用（SEC Fee・TAF 等）」は **要確認**のままで、`CostCalculator` は `commission + fxSpread` しか計算していない | 🔴 未供給と描く（**0 と書かない**） |
| 為替スプレッド相当額 | `CostCalculator` の `fxSpread` 項 | **出す** |
| 税 | `PnlSummary.TaxWithheld` | **出す** |
| 損益に対する費用率 | **分母の定義が計画に無い**（後述） | **出す（分母を明示する）＋ planning へ環流** |

🔴 **1 行目を「節ごと落とす」ことはしない。** 計画が求めた項目が出ていないことは、報告書自身に見えて
いなければならない（ADR-0030 決定3 と同じ理由）。**「0 件」とも書かない**——「損切りは 1 度も執行され
なかった」と読めるが、実際は「記録していない」である。

## 🔴 設計の中心: 内訳は帰属（1 回の畳み込み）から作る

**期間を切って `PnlAggregator.Aggregate` を呼び直さない**（IADR-0301 決定1）。費用の内訳は
スライス a が作った `FillPnlAttribution`（期間全体を 1 回だけ畳み込んだ約定単位の帰属）を
**そのまま数え直す**ことで作る。帰属は `Quantity`・`Price`・`Market` を持つため、
**フィールドを 1 つも増やさずに**手数料と為替スプレッドへ分解できる。

### `CostCalculator` への非破壊追加

```csharp
public readonly record struct OneWayCostBreakdown(decimal Commission, decimal FxSpread)
{
    public decimal Total => Commission + FxSpread;
}

public static OneWayCostBreakdown EstimateOneWayCostBreakdown(
    TradingAssumptions assumptions, Market market, decimal notional);
```

**既存の `EstimateOneWayCost` は本関数の `Total` を返す実装へ置き換える**（値は 1 円も変わらない・
式が 2 か所に分かれない）。既存の署名・呼び出し元は変えない。

### 新設する純関数

`backend/Services/ReportService/Domain/PeriodCostReview.cs`

```csharp
public sealed record PeriodCostReview(
    decimal Commission, decimal FxSpread, decimal TotalCost,
    decimal TaxWithheld, decimal RealizedPnlGross, decimal? CostRatio);

public static class PeriodCostReviewBuilder
{
    public static PeriodCostReview Build(
        IReadOnlyList<FillPnlAttribution> entries, TradingAssumptions assumptions, decimal taxWithheld);
}
```

- `Commission` / `FxSpread` / `TotalCost` / `RealizedPnlGross` は**帰属から数える**
  （`TotalCost` は §1 の費用合計と一致する。テストで固定する）。
- `TaxWithheld` は帰属からは出せない（税は期間合計にのみ課される）ため `PnlSummary` の値を受け取る。
- `CostRatio` が `null` は **「算出不能」**（分母 ≤ 0）であり、**未供給ではない**。

## 費用率の分母（計画に定義が無い・環流する）

計画は「**損益に対する費用率**」としか書いていない。本実装は次を採る。

> **費用率 = 費用合計 ÷ 実現損益（税引前・費用前）**

- **税引後・費用込みの実現損益（§1 の値）は分母に採れない**——費用と税を既に引いた値であり、
  費用が増えるほど分母が縮んで比率が跳ね上がる（**循環する**）。
- **分母が 0 以下のとき（損失の週・約定が無い週）は「算出不能」と描く。** `0%` とも「未供給」とも
  書かない——負の分母で割った比率は符号が反転し、「費用が少ない週」に見える。
- **計画に無い定義なので planning へ環流する**（起票前に同件を検索する）。裁定が出たら本実装を追随させる。

## 節の描画仕様

```markdown
## 5. リスク・費用レビュー

- 損切り執行: <未供給> / 発注拒否: <未供給> / 上限使用率の週間最大: <未供給>

| 費用の区分 | 金額 |
| --- | --- |
| 売買手数料 | +80.00 USD |
| 取引諸費用 | <未供給> |
| 為替スプレッド相当額 | +20.00 USD |
| 費用合計（§1 と同じ値） | +100.00 USD |
| 源泉徴収税額 | +380.00 USD |

- 損益に対する費用率: 5.0%（費用合計 +100.00 USD ÷ 実現損益〔税引前・費用前〕 +2,000.00 USD）
（＋凡例）
```

> 上の `<未供給>` は、実装では他節と同じ標識（`UnsuppliedCell`）を出す。本書では Markdown の強調記法が
> 入れ子になるのを避けるため、この表記で示している。

- 供給が無い（帰属を組み立てていない）ときは節の本文を
  「**費用の内訳を組み立てられませんでした（供給元がありません）**」とし、**「費用 0」と区別する**。
- 凡例で次を明記する。
  - **「取引諸費用」の記録源が無い**こと。したがって**費用合計は諸費用のぶんだけ過小である**こと。
  - 費用率の**分母は税引前・費用前の実現損益**であること（計画の定義が無く、実装が定めた）。
  - 1 行目の 3 項目が未供給である**それぞれの理由**。

## 影響範囲

| ファイル | 変更 |
| --- | --- |
| `Shared/AiStockTrading.Shared.Kernel/Trading/CostCalculator.cs` | 内訳版を**追加**（既存関数は内訳版へ委譲。値は不変） |
| `Services/ReportService/Domain/PeriodCostReview.cs` | **新規**（純関数） |
| `Services/ReportService/Domain/ReportView.cs` | `CostReview`（nullable）を追加 |
| `Services/ReportService/Domain/ReportRenderer.cs` | 週報 §5 を実体の描画へ置き換える（月報 §2 の未実装文言は残す） |
| `Services/ReportService/Features/Reports/ReportDraftService.cs` | 週報のときだけ費用レビューを組み立てて渡す |
| `Tests/Domain/Golden/weekly-supplied.md` / `weekly-unsupplied.md` | 更新（差分は**新設 1 節の中身だけ**） |
| `Tests/Domain/PeriodCostReviewTests.cs` | **新規**（内訳の和・費用率・算出不能） |
| `Tests/Domain/ReportRendererWeeklyBreakdownTests.cs` | §5 の焦点テストを追加 |
| `Tests/Features/Reports/ReportDraftWeeklyBreakdownTests.cs` | 出口（約定列 → 本文）の結線テストを追加 |

## テスト方針（受け入れ基準の写像）

1. 🔴 **内訳の和が §1 サマリの費用合計と一致する**——同じ約定列に対し `PnlAggregator.Aggregate` と
   `PeriodCostReviewBuilder.Build` を走らせ、`Commission + FxSpread == PnlSummary.TotalCost` を固定する。
   **JP（基準通貨外＝為替スプレッドあり）と US の両方を含む列で**行う（片方だけだと分解が効いていなくても緑になる）。
2. `EstimateOneWayCostBreakdown(...).Total == EstimateOneWayCost(...)`（既存関数の値が変わっていないこと）。
3. 費用率: 正の分母／**負の分母**／**0 の分母**（約定なし）の 3 通り。負・0 は「算出不能」であり `0%` ではない。
4. 出口の結線（`ReportDraftService` 経由で約定列から本文へ §5 が出る）。**レンダラ単体だけでは結線漏れを捕まえられない**（IADR-0269 決定1）。
5. ゴールデン 2 本で全文を固定する。**差分は §5 の中身だけ**であること。
6. 節番号の維持（§4・§6 が繰り上がっていないこと）。

## 母集合の取り方（是正・追随の対象）

- 未実装の理由定数の走査: `grep -n "PendingIssueReason" Domain/ReportRenderer.cs` → 宣言 1 ＋ 呼び出し 2
  （週報 §5・月報 §2）。**本 PR で外すのは週報 §5 の 1 つだけ**。定数は残す（スライス c が使う）。
- `EstimateOneWayCost` の呼び出し元の走査: `grep -rn "EstimateOneWayCost" --include=*.cs backend`
  → 呼び出し元は**一切変更しない**（署名も戻り値も変わらないため）。
- 除外したものと理由:
  - `docs/functional/` `docs/tests/`: **必須範囲外**（`docs/README.md` の網羅裁定は安全・統制の中核 FR に限る。
    FR-06/07/16/17 の報告書は含まれない）。作業仕様書と xUnit テストを正の記録とする。
  - `docs/api/openapi.yaml`: HTTP 契約は変わらない。

## 受け入れ基準

- [ ] 週報ゴールデン 2 本の「## 5. リスク・費用レビュー」に**中身**が出る（未実装文言が消える）。
- [ ] 費用の内訳（手数料・為替スプレッド）の和が §1 の費用合計と一致することがテストで固定されている。
- [ ] 「取引諸費用」が未供給の標識で出ており、`0` ではない。
- [ ] 費用率の分母が本文に明示され、分母 ≤ 0 では「算出不能」と描かれる。
- [ ] 1 行目の 3 項目が未供給の標識で出ており、それぞれの理由が凡例にある。
- [ ] **既存の §4・§6 の番号が変わらない**。月報 §2 は未実装のまま。
- [ ] planning へ費用率の分母定義を環流した（同件の既存 issue を検索したうえで）。
- [ ] `dotnet build` 警告 0・`dotnet format --verify-no-changes` 緑・文書系検査が緑。

## 残課題（本 PR では解かない）

- **月報 §2 週別・市場別の内訳**（スライス c）。#615 はここでは閉じない。
- **月報 §1 の「費用合計 / 費用率」**が `（データ連携後）` のまま残る。
- **損切り執行件数・発注拒否件数・上限使用率**の記録源。3 つとも本サービスの外に無い。
- **取引諸費用**の費用関数への算入（計画 `05_trading-assumptions` §2 が **要確認** のまま）。
