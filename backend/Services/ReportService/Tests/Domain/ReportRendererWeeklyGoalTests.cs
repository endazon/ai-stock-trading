using AwesomeAssertions;
using ReportService.Domain;
using Xunit;

namespace ReportService.Tests;

// T-06-081・T-06-084〜T-06-087・T-06-089, FR-06, FR-16, 計画 ADR-0059 決定 2〜4・ADR-0030 決定 3・5, #1218, IADR-0519 決定 4〜6:
// 日報 §6 振り返り（週次目標との照合）・週報 §1「週次目標に対する達成」・週報 §4 の照合の行の描画。全文の固定は ReportTemplateGoldenTests が担う。
public class ReportRendererWeeklyGoalTests
{
    private static readonly WeeklyGoalReference Reference =
        new("weekly-2026-W40", "weekly-2026-W40", WeeklyGoalLine.Parse("数値目標: -200 〜 +500 USD"));

    private static ReportView View(ReportKind kind, WeeklyGoalComparison? goal, string? review = null) => new()
    {
        Kind = kind,
        PeriodKey = kind == ReportKind.Weekly ? "weekly-2026-W41" : "daily-2026-10-06",
        PeriodLabel = kind == ReportKind.Weekly ? "2026-W41" : "2026-10-06",
        Pnl = new PnlSummary(0m, 0m, 0m, 0m, 0m, 0, 0, 0),
        PolicySummary = "方針",
        Narrative = "市況の散文",
        ReviewNarrative = review,
        WeeklyGoal = goal,
    };

    // 節の本文（見出しから次の `## ` 見出しまで）。後続の節の文言で緑にしない。
    private static string Section(string md, string heading)
    {
        var start = md.IndexOf(heading, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, heading);
        start += heading.Length;
        var next = md.IndexOf("\n## ", start, StringComparison.Ordinal);
        return next < 0 ? md[start..] : md[start..next];
    }

    private const string DailyReview = "## 6. 振り返り（週次目標との照合）";

    // ---- T-06-081: 日報 §6 は参照値・週初来の実現損益・位置を書く ----

    [Fact]
    public void 日報の振り返りは参照値と週初来の実現損益と位置を書き_散文を続ける()
    {
        var goal = WeeklyGoalComparison.Evaluate(Reference, WeeklyGoalActual.Of(-250m));

        var body = Section(ReportRenderer.RenderMarkdown(View(ReportKind.Daily, goal, "評価の文章。")), DailyReview);

        body.Should().Contain("- 週次目標: -200.00 USD 〜 +500.00 USD（weekly-2026-W40 の §6 の数値目標）に対し、"
            + "週初来の実現損益（税引後・費用込み）は -250.00 USD で **下限を下回る（下限との差 -50.00 USD）**です。")
            .And.Contain("週報 §1 の「週間実現損益（税引後・費用込み）」と同じ定義")
            .And.Contain("**達成・未達は判定していません**")
            .And.Contain("評価の文章。");
        body.Should().NotContain("本節は未実装です");
    }

    // ---- T-06-083: 注記・週次目標なし ----

    [Fact]
    public void 前週の週報でない目標と照らしたら注記し_確定済みが無ければ週次目標なしと書く()
    {
        var fallback = WeeklyGoalComparison.Evaluate(
            new WeeklyGoalReference("weekly-2026-W40", "weekly-2026-W38", Reference.Reading), WeeklyGoalActual.Of(0m));
        var none = WeeklyGoalComparison.Evaluate(WeeklyGoalReference.None("weekly-2026-W40"), WeeklyGoalActual.Of(0m));

        Section(ReportRenderer.RenderMarkdown(View(ReportKind.Daily, fallback)), DailyReview)
            .Should().Contain("- 注記: 前週の週報（weekly-2026-W40）が確定していないため、最新の確定済み週報 weekly-2026-W38 の目標と照らしています。");
        Section(ReportRenderer.RenderMarkdown(View(ReportKind.Daily, none)), DailyReview)
            .Should().Contain("- 週次目標: **週次目標なし**（確定済みの週報がありません）。照合していません。")
            .And.NotContain("範囲内");
    }

    // ---- T-06-084: 照合不能 ----

    [Theory]
    [InlineData("押し目買い")]
    [InlineData("数値目標: -30,000 〜 +75,000 円")]
    public void 書式どおりでなければ照合不能と書き_範囲と位置を書かない(string policy)
    {
        var goal = WeeklyGoalComparison.Evaluate(
            new WeeklyGoalReference("weekly-2026-W40", "weekly-2026-W40", WeeklyGoalLine.Parse(policy)), WeeklyGoalActual.Of(100m));

        var body = Section(ReportRenderer.RenderMarkdown(View(ReportKind.Daily, goal)), DailyReview);

        body.Should().Contain("**照合不能**").And.Contain("照合していません。");
        body.Should().NotContain("範囲内").And.NotContain("下限").And.NotContain("+100.00 USD");
    }

    // ---- T-06-085: 算出不能 ----

    [Fact]
    public void 週初来の実現損益が算出不能なら理由を書き_位置を書かない()
    {
        var goal = WeeklyGoalComparison.Evaluate(Reference, WeeklyGoalActual.NotComputable("週初来の約定を照会できませんでした"));

        var body = Section(ReportRenderer.RenderMarkdown(View(ReportKind.Daily, goal)), DailyReview);

        body.Should().Contain("は **算出不能**（週初来の約定を照会できませんでした）。照合していません。**0 ではありません。**");
        body.Should().NotContain("範囲内");
    }

    // ---- T-06-086（否定形）: §5 と §6 を統合しない・散文を複製しない ----

    [Fact]
    public void 市況の散文を振り返りへ流し込まない()
    {
        var goal = WeeklyGoalComparison.Evaluate(Reference, WeeklyGoalActual.Of(0m));

        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily, goal, review: null));

        Section(md, "## 5. 市況・特記事項").Should().Contain("市況の散文");
        Section(md, DailyReview).Should().NotContain("市況の散文").And.Contain("（散文ドラフトなし）");
        md.Should().NotContain("市況・振り返り");
        md.IndexOf("## 5. 市況・特記事項", StringComparison.Ordinal).Should().BeLessThan(md.IndexOf(DailyReview, StringComparison.Ordinal));
        md.IndexOf(DailyReview, StringComparison.Ordinal).Should().BeLessThan(md.IndexOf("## 7. 翌営業日の方針", StringComparison.Ordinal));
    }

    // ---- T-06-087: 週報 §1 は判定保留で位置を示し、§4 は散文の前に照合の行 ----

    [Fact]
    public void 週報のサマリは達成未達を書かず判定保留と位置を示し_振り返りに照合の行を置く()
    {
        var goal = WeeklyGoalComparison.Evaluate(Reference, WeeklyGoalActual.Of(120m));

        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Weekly, goal));

        md.Should().Contain("| 週次目標に対する達成 | **判定保留**（達成・未達を範囲のどこで分けるかが計画で未決です）— "
            + "週間実現損益は目標 -200.00 USD 〜 +500.00 USD（weekly-2026-W40 の §6 の数値目標）の **範囲内**（詳細は §4） |");
        var summaryRow = md.Split('\n').Single(l => l.StartsWith("| 週次目標に対する達成", StringComparison.Ordinal));
        summaryRow.Replace("達成・未達", string.Empty, StringComparison.Ordinal).Replace("週次目標に対する達成", string.Empty, StringComparison.Ordinal)
            .Should().NotContain("達成").And.NotContain("未達");

        var review = Section(md, "## 4. 振り返りと評価");
        review.Should().Contain("- 週次目標に対する結果: -200.00 USD 〜 +500.00 USD（weekly-2026-W40 の §6 の数値目標）に対し、"
            + "週間実現損益（税引後・費用込み）は +120.00 USD で **範囲内**です。");
        review.IndexOf("週次目標に対する結果", StringComparison.Ordinal).Should().BeLessThan(review.IndexOf("市況の散文", StringComparison.Ordinal));
    }

    [Fact]
    public void 週報のサマリは照合不能と週次目標なしと算出不能を書き分ける()
    {
        string Cell(WeeklyGoalComparison goal) => ReportRenderer.RenderMarkdown(View(ReportKind.Weekly, goal)).Split('\n')
            .Single(l => l.StartsWith("| 週次目標に対する達成", StringComparison.Ordinal));

        Cell(WeeklyGoalComparison.Evaluate(WeeklyGoalReference.None("weekly-2026-W40"), WeeklyGoalActual.Of(0m))).Should().Contain("週次目標なし");
        Cell(WeeklyGoalComparison.Evaluate(
                new WeeklyGoalReference("weekly-2026-W40", "weekly-2026-W40", WeeklyGoalLine.Parse("押し目買い")), WeeklyGoalActual.Of(0m)))
            .Should().Contain("照合不能");
        Cell(WeeklyGoalComparison.Evaluate(Reference, WeeklyGoalActual.NotComputable("x"))).Should().Contain("**判定保留** — 週間実現損益が算出不能");
    }

    // ---- T-06-089: 照会していない経路 ----

    [Fact]
    public void 照会していない経路は照会していませんと書き_週次目標なしと混ぜない()
    {
        var daily = ReportRenderer.RenderMarkdown(View(ReportKind.Daily, null));
        var weekly = ReportRenderer.RenderMarkdown(View(ReportKind.Weekly, null));

        Section(daily, DailyReview).Should().Contain("**照会していません**").And.NotContain("週次目標なし（");
        weekly.Should().Contain("| 週次目標に対する達成 | **照会していません**");
        Section(weekly, "## 4. 振り返りと評価").Should().Contain("**照会していません**");
    }
}
