---
title: 週報 §2 日別推移・§3 ハイライト取引を、約定単位の損益帰属（1 回の畳み込み）から描く
type: spec
status: review
related_ids: [FR-06, FR-07, FR-16, UC-05, ADR-0030, IADR-0025, IADR-0033, IADR-0269, IADR-0291]
author: endazon (with Claude Code)
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0030_report-section-numbering-is-plan-canonical.md
---

# 仕様書: 週報 §2 日別推移・§3 ハイライト取引を、約定単位の損益帰属（1 回の畳み込み）から描く

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-06（報告サイクル）／FR-07（報告書の階層管理）／FR-16（報告書テンプレート）
- ユースケース（UC）: UC-05（報告書の確定）
- 画面（SC）: なし
- 関連 ADR: 計画 ADR-0030（節番号・節順は計画が正・未実装の節は詰めない）
- 関連 IADR: [IADR-0291](../adr/IADR-0291_report-sections-follow-plan-numbering.md)（未実装節の見出し出力。本作業はその
  フォローアップ 1）／[IADR-0269](../adr/IADR-0269_trade-history-wiring-and-record-based-supply.md)（記録の転記・未供給の規律）／
  [IADR-0033](../adr/IADR-0033_shared-inventory-fold.md)（符号付き在庫の単一情報源）／
  [IADR-0025](../adr/IADR-0025_pnl-aggregation.md)（損益集計の純関数）
- 関連する作業仕様書: [20260902_612_report-section-structure-survey](./20260902_612_report-section-structure-survey.md)（全数突合表）
- 計画書リンク: `project-planning/projects/ai-stock-trading/06_technical/04_report-templates.md` §週報テンプレート
  （隣接クローンで確認。GitHub 上の相当パス:
  `https://github.com/endazon/project-planning/blob/main/projects/ai-stock-trading/06_technical/04_report-templates.md`）

## 目的・背景

[#615](https://github.com/endazon/ai-stock-trading/issues/615) は、計画が定める節のうち実装に無い 4 節
（週報 §2 日別推移・§3 ハイライト取引・§5 リスク・費用レビュー、月報 §2 週別・市場別の内訳）を実装する。
IADR-0291 で見出しだけは出るようになったが、本文は「本節は未実装です（#615 で実装予定）」のままである。

**本作業（スライス a）は週報 §2・§3 に限る。** 週報 §5 と月報 §2 は別 PR（同じ #615）とし、本 PR では触れない。
月報 §3 税金レビュー・日報 §6 は別の理由定数で分離済みであり #615 の対象外である。

## 対象範囲

- 対象:
  - 新規の Domain 純関数（約定単位の損益帰属）とその集計（日別・ハイライト）。
  - `ReportView` への入力フィールド追加、`ReportDraftService` の種別ゲート結線。
  - `ReportRenderer` の週報 §2・§3 を `AppendNotImplemented` から実体の描画へ置き換える。
  - ゴールデン 2 本（`Tests/Domain/Golden/weekly-{supplied,unsupplied}.md`）の更新と焦点テストの追加。
- 対象外:
  - **週報 §5 リスク・費用レビュー**（スライス b）・**月報 §2 週別・市場別の内訳**（スライス c）。
    本 PR は `AppendNotImplemented` の呼び出しを残す。
  - 月報 §3 税金レビュー・日報 §6 振り返り（別の理由定数。#615 の対象外）。
  - 「上限使用率の週間最大」（算出元が本サービスにも取引管理サービスにも無い共通ギャップ。#615 が明示的に対象外）。
  - 新しい HTTP ポート・イベント・設定。**1 つも要らない**（後述「入力の所在」）。
  - 節番号の繰り上げ。**既存の §4・§6 は計画正のまま動かさない**（ADR-0030 決定1・決定2・IADR-0291 決定1）。

## 入力の所在（新しい配管が要らない根拠）

`ReportAutoGenerator.SafeFillsAsync` / `SafeRationalesAsync` は**種別非依存**で `IPeriodFillSource` /
`ITradeRationaleSource` を呼び、`DraftRequest.Fills` / `DraftRequest.TradeRationales` に載せている（実測:
`Features/Reports/ReportAutoGenerator.cs` の `BuildDraftAsync` 呼び出しは日報・週報・月報で同じ引数を渡す）。
`ReportDraftService` は `fills` を `PnlAggregator` へ渡して §1 を作っている。**本作業は同じ入力から別の
帰属を作るだけである。**

## 🔴 設計の中心: 畳み込みは期間全体で 1 回だけ

**期間を切って `PnlAggregator.Aggregate` を呼び直す実装は採らない。**

`PnlAggregator` は期間全体を `SignedInventory.Apply`（IADR-0033）で畳み込む。日・週・市場でスライスして
呼び直すと、**持ち越し建玉の平均取得単価がスライス内に存在しない**ため、スライスの合計が §1 サマリの合計と
一致しなくなる。しかも**全テストが緑のままそうなる**（各スライスは自分の中では整合しているため）。

正しい形は既存の `TradeHistoryViewBuilder`（日報 §2）と同型である ——
**期間全体を 1 回だけ畳み込み、決済（在庫が減る約定）の実現損益をその約定の日／週／市場／方向へ帰属させる。**

### 新設する純関数

`backend/Services/ReportService/Domain/FillPnlAttribution.cs`

```csharp
public sealed record FillPnlAttribution(
    int Sequence, DateTimeOffset ExecutedAt, DateOnly SessionDateJst,
    Market Market, string Symbol, TradeSide Side, int Quantity, decimal Price,
    decimal Cost, decimal RealizedPnlGross, bool Realizing, string? Rationale);

public static class FillPnlAttributionBuilder
{
    public static IReadOnlyList<FillPnlAttribution> Build(
        IReadOnlyList<PeriodTradeFill> fills, TradingAssumptions assumptions,
        IReadOnlyDictionary<Guid, string>? rationales);

    public static IReadOnlyList<DailyPnlRow> ByDay(IReadOnlyList<FillPnlAttribution> entries);
    public static TradeHighlights Highlights(IReadOnlyList<FillPnlAttribution> entries);
}
```

- **粒度は「1 約定 = 1 件」**（決済に至らない新規建ても含む）。日・週・市場・方向のいずれにも集計できる。
  - 日別 = `SessionDateJst`／週別 = `SessionDateJst` の ISO 週／市場別 = `Market`。
  - **方向別（ロング/ショート）は追加の列を持たない。** 決済は必ず反対方向の約定であるため、
    `Realizing && Side == Sell` ⇒ ロングの決済、`Realizing && Side == Buy` ⇒ ショートの決済で**一意に決まる**。
    畳み込みを見なくても導けるので、フィールドを増やさない（スライス c は再畳み込みしなくてよい）。
- **費用は `CostCalculator.EstimateOneWayCost`**（`PnlAggregator` と同じ関数・同じ引数）。
- **判断根拠は記録の転記**（`TradeHistoryViewBuilder` と同じ規則。相関できない約定は `null`＝未供給）。

## 節ごとの描画仕様

### 週報 §2 日別推移

計画の表定義（`| 日付 | 実現損益 | 取引数 | 主な要因 |`）をそのまま採る。

| 列 | 値 | 根拠 |
| --- | --- | --- |
| 日付 | `SessionDateJst`（JST） | 台帳の `ExecutedAt` を JST へ寄せる（日報 §2 と同じ基準） |
| 実現損益 | **税引前・費用込み** = 当日の決済損益（gross）− 当日の約定に掛かる概算費用 | 源泉徴収税額は**期間合計にのみ**課され、日へ配分する規則が無い（日報 §2 の税列が未供給なのと同じ理由） |
| 取引数 | **約定件数**（新規建てを含む） | §1 の「取引回数（買/売/決済）」の 買＋売 に一致する |
| 主な要因 | **当日の実現損益への寄与が最大の決済**（銘柄と損益）。決済が無い日は「決済なし（新規建てのみ）」 | 散文の要因説明を持つ記録源が無い。**LLM に書かせない**（FR-16） |

- **約定が 1 件も無い日は行を出さない**（決定は IADR へ）。
- 凡例で「本節の実現損益の合計 − §1 の源泉徴収税額 = §1 の週間実現損益」と明記する（読み手が突合できる）。

### 週報 §3 ハイライト取引

計画の形（`- 最良: …` / `- 最悪: …`）をそのまま採る。**決済（`Realizing`）の中から**最大・最小を選ぶ。

- 損益は**税引前・費用前**（当該決済の約定代金差額）。費用・税は期間合計にのみ集計され、約定単位へ配分する規則が無い。
- 「該当日報リンク」は**リンクではなく報告書の自然キー**（`ReportPeriod.ExpectedKey` の日報キー）とする。
  報告書に URL 体系が無いため。
- 「判断の要点」「原因」は**記録の転記**。相関できない決済は `**未供給**`。
- **決済が 0 件のときは「決済取引がありません」**と書き、「損益 0」と区別する。
- **最良と最悪が同一の約定になる場合はその旨を明記する**（隠すと 2 件あったように読める）。

### 同値時の決定規則（実行ごとに並びが変わらないこと）

`(実現損益, 約定時刻, 銘柄コード〔序数比較〕, 市場, 畳み込み順序)` の辞書式で全順序を作る。
最良は降順の先頭、最悪は昇順の先頭。**辞書・ハッシュの列挙順序には一切依存しない。**

## 影響範囲

| ファイル | 変更 |
| --- | --- |
| `Domain/FillPnlAttribution.cs` | **新規**（純関数＋日別集計＋ハイライト抽出） |
| `Domain/ReportView.cs` | `FillAttributions`（nullable）を追加。`null`＝組み立てていない／空列＝約定なし |
| `Domain/ReportRenderer.cs` | 週報 §2・§3 を実体の描画へ置き換える（§5 の未実装文言は残す） |
| `Features/Reports/ReportDraftService.cs` | 週報のときだけ帰属を組み立てて渡す |
| `Tests/Domain/Golden/weekly-supplied.md` / `weekly-unsupplied.md` | 更新（差分は**新設 2 節の中身だけ**） |
| `Tests/Domain/FillPnlAttributionTests.cs` | **新規**（合計一致・順序・帰属） |
| `Tests/Domain/ReportRendererWeeklyBreakdownTests.cs` | **新規**（焦点テスト） |

## テスト方針（受け入れ基準の写像）

1. **内訳の和が §1 サマリの合計と一致する**（畳み込みが 1 回であることの証跡）。
   同じ約定列に対し帰属の生成と `PnlAggregator.Aggregate` を走らせ、
   Σ`RealizedPnlGross` / Σ`Cost` / 件数 / 決済件数 / 勝ち決済件数 が `PnlSummary` と一致することを固定する。
   **日別集計の和でも同じ突合を行う**（グルーピングで行が落ちても捕まえる）。
2. **持ち越し建玉があっても一致する**ケースを含める（期間の前半で建てた玉を後半で決済する形）。
   ここが「期間を切って呼び直す」実装との差が出る唯一の場所である。
3. **同値の決済が複数あっても最良・最悪が一意**（入力順を入れ替えても同じ結果）。
4. **ゴールデン 2 本**（供給あり／なし）で全文を固定する。**結線を外すと赤くなる**（IADR-0269 決定1 と同じ理由）。
5. 節番号の維持（§4・§6 が繰り上がっていないこと）をゴールデンで固定する。

## 母集合の取り方（是正・追随の対象）

- 未実装の理由定数を使う節の走査: `grep -n "PendingIssueReason" Domain/ReportRenderer.cs` → 5 箇所
  （定数の宣言 1 と呼び出し 4＝週報 §2・§3・§5、月報 §2）。**本 PR で外すのは呼び出しの前 2 つだけ**であり、
  残る 2 つは定数ごと残す（定数を消すとスライス b・c が文言を失う）。
- ゴールデンの走査: `grep -rln "615" Tests/Domain/Golden` → `weekly-supplied.md` / `weekly-unsupplied.md` /
  `monthly-supplied.md` / `monthly-unsupplied.md` の 4 本。**本 PR で変わるのは週報 2 本だけ**（月報は §2 の文言が残る）。
- 除外したものと理由:
  - `docs/functional/` `docs/tests/`: **必須範囲外**。`docs/README.md` の網羅裁定は機能仕様書・テスト仕様書の
    必須範囲を「リスク統制・ペーパートレード・バックテスト・取引ガード・段階ゲート」に限っており、
    FR-06/07/16（報告書）は含まれない。作業仕様書と xUnit テストを正の記録とする。
  - `docs/api/openapi.yaml`: HTTP 契約は変わらない（新しい端点・要求項目が無い）。

## 受け入れ基準

- [ ] 週報ゴールデン 2 本に「## 2. 日別推移」「## 3. ハイライト取引」の**中身**が出る（未実装文言が消える）。
- [ ] **既存の §4・§6 の番号が変わらない**（計画正のまま）。
- [ ] 日別推移の実現損益・件数の和が §1 サマリと一致することがテストで固定されている。
- [ ] ハイライトの同値時の並びが入力順に依存しない。
- [ ] 未供給と 0・「該当なし」を区別している（`null` と空列を潰さない）。
- [ ] `dotnet build` 警告 0・`dotnet format --verify-no-changes` 緑・文書系検査が緑。

## 残課題（本 PR では解かない）

- 週報 §5 リスク・費用レビュー（スライス b）・月報 §2 週別・市場別の内訳（スライス c）。#615 は閉じない。
- 「主な要因」の**散文**（機械的な寄与最大の決済で代替している）。要因の記録源が生まれたら差し替える。
- 「該当日報リンク」の URL 化（報告書に URL 体系が無い）。
