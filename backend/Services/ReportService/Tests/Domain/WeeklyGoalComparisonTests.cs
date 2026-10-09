using AwesomeAssertions;
using ReportService.Domain;
using ReportService.Features.Reports;
using Xunit;

namespace ReportService.Tests;

// T-06-081・T-06-083〜T-06-085, FR-06, FR-16, 計画 ADR-0059 決定 2〜4, #1218, IADR-0519 決定 4: 週次目標の照合（純関数）。
// 比較はコード（範囲の両端ちょうどは範囲内・丸めずに比べる）。🔴 達成・未達は判定しない。
public class WeeklyGoalComparisonTests
{
    private static WeeklyGoalReference Goal(string policy, string source = "weekly-2026-W40") =>
        new("weekly-2026-W40", source, WeeklyGoalLine.Parse(policy));

    private const string Range = "数値目標: -200 〜 +500 USD";

    // ---- T-06-081: 位置と差 ----

    [Theory]
    [InlineData(-200.01, WeeklyGoalPosition.BelowRange)]
    [InlineData(-200, WeeklyGoalPosition.WithinRange)]
    [InlineData(0, WeeklyGoalPosition.WithinRange)]
    [InlineData(500, WeeklyGoalPosition.WithinRange)]
    [InlineData(500.001, WeeklyGoalPosition.AboveRange)]
    public void 範囲の両端ちょうどは範囲内で_外は近い端との差を出す(double actual, WeeklyGoalPosition expected)
    {
        var result = WeeklyGoalComparison.Evaluate(Goal(Range), WeeklyGoalActual.Of((decimal)actual));

        result.Outcome.Should().Be(WeeklyGoalOutcome.Compared);
        result.Position.Should().Be(expected);
        result.DistanceOutside.Should().Be(expected switch
        {
            WeeklyGoalPosition.BelowRange => (decimal)actual + 200m,
            WeeklyGoalPosition.AboveRange => (decimal)actual - 500m,
            _ => null,
        });
    }

    [Fact]
    public void 表記は参照した週報と範囲と位置を書き_達成未達を書かない()
    {
        var below = WeeklyGoalComparison.Evaluate(Goal(Range), WeeklyGoalActual.Of(-250m));

        below.GoalText.Should().Be("-200.00 USD 〜 +500.00 USD（weekly-2026-W40 の §6 の数値目標）");
        below.PositionText.Should().Be("下限を下回る（下限との差 -50.00 USD）");
        WeeklyGoalComparison.Evaluate(Goal(Range), WeeklyGoalActual.Of(600m)).PositionText.Should().Be("上限を上回る（上限との差 +100.00 USD）");
        below.PositionText.Should().NotContain("達成").And.NotContain("未達");
        below.UnavailableText.Should().BeNull();
        below.FallbackNote.Should().BeNull();
    }

    // ---- T-06-083: 前週の週報が未確定なら最新の確定済みを注記つきで・確定済みが無ければ週次目標なし ----

    [Fact]
    public void 前週の週報でない目標を使ったら注記する()
    {
        var result = WeeklyGoalComparison.Evaluate(Goal(Range, source: "weekly-2026-W38"), WeeklyGoalActual.Of(0m));

        result.Reference.IsFallback.Should().BeTrue();
        result.FallbackNote.Should().Contain("weekly-2026-W40").And.Contain("weekly-2026-W38");
    }

    [Fact]
    public void 確定済みの週報が無ければ週次目標なし()
    {
        var result = WeeklyGoalComparison.Evaluate(WeeklyGoalReference.None("weekly-2026-W40"), WeeklyGoalActual.Of(0m));

        result.Outcome.Should().Be(WeeklyGoalOutcome.NoGoal);
        result.Position.Should().BeNull();
        result.UnavailableText.Should().Contain("週次目標なし");
        result.GoalText.Should().BeNull();
    }

    // ---- T-06-084: 行が無い・書式外・単位違いは照合不能（実績を見ない） ----

    [Theory]
    [InlineData("押し目買い", "「数値目標:」行がありません")]
    [InlineData("数値目標: +500 USD", "書式に合わないか")]
    [InlineData("数値目標: -30,000 〜 +75,000 円", "基準通貨 USD ではありません〔円〕。換算しません")]
    public void 書式どおりの行が無ければ照合不能(string policy, string reason)
    {
        var result = WeeklyGoalComparison.Evaluate(Goal(policy), WeeklyGoalActual.Of(100m));

        result.Outcome.Should().Be(WeeklyGoalOutcome.NotComparable);
        result.Position.Should().BeNull();
        result.UnavailableText.Should().Contain("照合不能").And.Contain(reason).And.Contain("weekly-2026-W40");
        result.GoalText.Should().BeNull("照合不能の目標の範囲を書かない（単位違いの数値を USD と並べない）");
    }

    // ---- T-06-085: 実績が部分値なら算出不能 ----

    [Fact]
    public void 実績が部分値なら算出不能で位置を出さない()
    {
        var unvalued = new PnlSummary(10m, 1m, 0m, 9m, 0m, 2, 1, 1, UnvaluedSettlementCount: 1);
        var unknownOpening = new PnlSummary(10m, 1m, 0m, 9m, 0m, 2, 1, 1, OpeningInventoryUnknown: true);

        var a = WeeklyGoalComparison.Evaluate(Goal(Range), WeeklyGoalActual.From(unvalued));
        var b = WeeklyGoalComparison.Evaluate(Goal(Range), WeeklyGoalActual.From(unknownOpening));

        a.Outcome.Should().Be(WeeklyGoalOutcome.NotComputable);
        a.Actual.NotComputableReason.Should().Contain("1 件");
        b.Outcome.Should().Be(WeeklyGoalOutcome.NotComputable);
        b.Actual.NotComputableReason.Should().Contain("期間開始時点の在庫");
        a.Position.Should().BeNull();
        WeeklyGoalActual.From(new PnlSummary(10m, 1m, 0m, 9m, 0m, 2, 1, 1)).Amount.Should().Be(9m, "部分値でなければ税引後・費用込みの実現損益");
    }
}

// T-06-086, FR-06, 計画 ADR-0030 決定 5・ADR-0059 フォローアップ 4, #1218, IADR-0519 決定 4, IADR-0291 決定 4: 日報の散文を §5 と §6 に分ける（複製しない）。
public class DailyNarrativeSectionsTests
{
    [Fact]
    public void 区切り行で市況と振り返りに分ける()
    {
        var (market, review) = DailyNarrativeSections.Split($"市況の文。\n特記事項。\n  {DailyNarrativeSections.Marker}  \n振り返りの文。");

        market.Should().Be("市況の文。\n特記事項。");
        review.Should().Be("振り返りの文。");
    }

    // （否定形）区切り行が無ければ全文を §5 に置き、§6 へ複製しない。
    [Fact]
    public void 区切り行が無ければ全文を市況に置き_振り返りは無い()
    {
        var (market, review) = DailyNarrativeSections.Split("市況と振り返りが混ざった文。");

        market.Should().Be("市況と振り返りが混ざった文。");
        review.Should().BeNull();
        DailyNarrativeSections.Split(ReportNarrativeDefaults.PlaceholderText)
            .Should().Be((ReportNarrativeDefaults.PlaceholderText, (string?)null), "LLM 未接続の定型文は §5 のまま");
    }

    [Fact]
    public void 区切りの後が空なら振り返りは無く_2つ目の区切り行は除く()
    {
        DailyNarrativeSections.Split($"市況。\n{DailyNarrativeSections.Marker}\n").Review.Should().BeNull();
        DailyNarrativeSections.Split($"市況。\n{DailyNarrativeSections.Marker}\n振り返り。\n{DailyNarrativeSections.Marker}\n続き。").Review
            .Should().Be("振り返り。\n続き。");
        DailyNarrativeSections.Split($"本文中の {DailyNarrativeSections.Marker} は区切りではない").Review.Should().BeNull();
    }
}
