using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-07, FR-16, #615, IADR-0301, 04_report-templates 週報 §2 / §3:
// **出口（`ReportRenderer` の本文）で** 日別推移とハイライト取引が出ることを固定する。
//
// 🔴 **純関数のテスト（FillPnlAttributionTests）だけでは、結線が無くても緑になる**——
// それが #563 で実際に起きた形である（IADR-0269 決定1）。ここでは出口の本文だけを見る。
// 全文の固定は `ReportTemplateGoldenTests`（weekly-supplied.md / weekly-unsupplied.md）が担う。
public class ReportRendererWeeklyBreakdownTests
{
    private static readonly DateTimeOffset Mon = new(2026, 8, 24, 9, 5, 0, TimeSpan.FromHours(9));

    private static ReportView Weekly(IReadOnlyList<FillPnlAttribution>? attributions) => new()
    {
        Kind = ReportKind.Weekly,
        PeriodKey = "weekly-2026-W35",
        PeriodLabel = "2026-W35",
        Markets = ["JP", "US"],
        AssumptionsVersion = 3,
        Pnl = new PnlSummary(2_000m, 100m, 380m, 1_520m, 0m, 4, 2, 1),
        Narrative = "散文。",
        PolicySummary = "方針。",
        FillAttributions = attributions,
    };

    private static FillPnlAttribution Entry(
        int sequence, int addDays, string symbol, Market market, TradeSide side,
        decimal realized, bool realizing, string? rationale = null) =>
        new(sequence, Mon.AddDays(addDays), DateOnly.FromDateTime(Mon.AddDays(addDays).DateTime),
            market, symbol, side, Quantity: 100, Price: 2_500m, Cost: 120m,
            RealizedPnlGross: realized, Realizing: realizing, Rationale: rationale);

    private static IReadOnlyList<FillPnlAttribution> Week() =>
    [
        Entry(1, 0, "7203", Market.Japan, TradeSide.Buy, 0m, realizing: false, "始値が支持線で反発。"),
        Entry(2, 2, "AAPL", Market.UnitedStates, TradeSide.Sell, 250m, realizing: true, "目標価格へ到達。"),
        Entry(3, 3, "7203", Market.Japan, TradeSide.Sell, -2_000m, realizing: true),
    ];

    // --- 結線（出口に出ること） ---

    [Fact]
    public void 週報の本文に日別推移とハイライト取引が出る()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly(Week()));

        md.Should().Contain("## 2. 日別推移");
        md.Should().Contain("| 日付 | 実現損益 | 取引数 | 主な要因 |");
        md.Should().Contain("## 3. ハイライト取引");
        md.Should().Contain("- **最良**:");
        md.Should().Contain("- **最悪**:");
        // 未実装の文言が §2・§3 から消えていること（§5 には残る＝別スライス）。
        Section(md, "## 2. 日別推移", "## 4. 振り返りと評価").Should().NotContain("本節は未実装です");
    }

    // 🔴 ADR-0030 決定1・決定2 / IADR-0291: **既存の節番号を繰り上げない。**
    [Fact]
    public void 既存の節番号を繰り上げない()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly(Week()));

        md.Should().Contain("## 1. 週間サマリ");
        md.Should().Contain("## 4. 振り返りと評価");
        // §5 は #615 スライス b で実体化した（ReportRendererRiskCostReviewTests が中身を見る）。
        md.Should().Contain("## 5. リスク・費用レビュー");
        md.Should().Contain("## 6. 翌週の方針");
    }

    [Fact]
    public void 週報以外では日別推移とハイライト取引を出さない()
    {
        // 供給しても日報・月報の本文には出ない（計画の粒度対応表が週報に置いている）。
        foreach (var kind in new[] { ReportKind.Daily, ReportKind.Monthly })
        {
            var md = ReportRenderer.RenderMarkdown(Weekly(Week()) with
            {
                Kind = kind,
                PeriodKey = kind == ReportKind.Daily ? "daily-2026-08-28" : "monthly-2026-08",
                PeriodLabel = kind == ReportKind.Daily ? "2026-08-28" : "2026-08",
            });

            md.Should().NotContain("## 2. 日別推移");
            md.Should().NotContain("## 3. ハイライト取引");
        }
    }

    // --- 未供給・空列・0 件の区別（潰さない） ---

    [Fact]
    public void 帰属が未供給なら約定なしと区別して節ごと出す()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly(null));

        md.Should().Contain("## 2. 日別推移");
        md.Should().Contain("**日別の内訳を組み立てられませんでした（供給元がありません）**");
        md.Should().Contain("## 3. ハイライト取引");
        md.Should().Contain("**ハイライトを組み立てられませんでした（供給元がありません）**");
        md.Should().NotContain("（当週の約定なし）");
    }

    [Fact]
    public void 約定が0件なら未供給と区別して約定なしと書く()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly([]));

        md.Should().Contain("（当週の約定なし）");
        md.Should().Contain("（当週に決済取引はありません）");
        // 🔴 §2・§3 の中だけを見る（本フィクスチャは §5 の費用レビューを供給しておらず、
        // そちらは正しく「供給元がありません」と書くため。全文で見ると別の節の文言を拾う）。
        Section(md, "## 2. 日別推移", "## 4. 振り返りと評価").Should().NotContain("供給元がありません");
    }

    [Fact]
    public void 新規建てだけの週は決済取引が無いと書き損益0と区別する()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly(
            [Entry(1, 0, "7203", Market.Japan, TradeSide.Buy, 0m, realizing: false)]));

        md.Should().Contain("（当週に決済取引はありません）");
        md.Should().Contain("決済なし（新規建てのみ）");
        md.Should().Contain("**「損益 0」でも未供給でもありません**");
    }

    // --- 内容 ---

    [Fact]
    public void 日別推移は約定のあった日だけを日付昇順で出す()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly(Week()));

        var progression = Section(md, "## 2. 日別推移", "## 3. ハイライト取引");
        progression.Should().Contain("| 2026-08-24 |");
        progression.Should().Contain("| 2026-08-26 |");
        progression.Should().Contain("| 2026-08-27 |");
        // 火曜（08-25）は約定が無いため行そのものが無い。
        progression.Should().NotContain("| 2026-08-25 |");
        progression.IndexOf("| 2026-08-24 |", StringComparison.Ordinal)
            .Should().BeLessThan(progression.IndexOf("| 2026-08-27 |", StringComparison.Ordinal));
    }

    [Fact]
    public void 日別推移の実現損益は税引前かつ費用込みであることを凡例で明示する()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly(Week()));

        md.Should().Contain("「実現損益」は**税引前・費用込み**");
        md.Should().Contain("**源泉徴収税額は期間合計にのみ課され、日へ配分する規則がありません**");
    }

    [Fact]
    public void ハイライトは銘柄と損益と該当日報の自然キーを出す()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly(Week()));

        var highlights = Section(md, "## 3. ハイライト取引", "## 4. 振り返りと評価");
        highlights.Should().Contain("AAPL（US）");
        highlights.Should().Contain("`daily-2026-08-26`");
        highlights.Should().Contain("7203（JP）");
        highlights.Should().Contain("`daily-2026-08-27`");
        // 判断の要点は記録の転記。相関できなかった決済は未供給と書く（0・「なし」と区別）。
        highlights.Should().Contain("判断の要点: 目標価格へ到達。");
        highlights.Should().Contain("原因: **未供給**");
    }

    [Fact]
    public void 決済が1件だけなら最良と最悪が同一である旨を明記する()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly(
        [
            Entry(1, 0, "7203", Market.Japan, TradeSide.Buy, 0m, realizing: false),
            Entry(2, 1, "7203", Market.Japan, TradeSide.Sell, 500m, realizing: true),
        ]));

        md.Should().Contain("**当週の決済は 1 件のみ**のため、最良と最悪は**同一の取引**です。");
    }

    [Fact]
    public void 決済がすべて同額なら同一の取引を選んだ旨を明記する()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly(
        [
            Entry(1, 0, "7203", Market.Japan, TradeSide.Sell, 500m, realizing: true),
            Entry(2, 1, "AAPL", Market.UnitedStates, TradeSide.Sell, 500m, realizing: true),
        ]));

        md.Should().Contain("**当週の決済はすべて同額**のため、最良と最悪に**同一の取引**を選んでいます");
    }

    private static string Section(string markdown, string from, string to)
    {
        var start = markdown.IndexOf(from, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = markdown.IndexOf(to, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return markdown[start..end];
    }
}
