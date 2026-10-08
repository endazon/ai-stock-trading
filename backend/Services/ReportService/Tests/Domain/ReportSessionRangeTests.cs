using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using ReportService.Domain;
using ReportService.Features.Reports;
using Xunit;

namespace ReportService.Tests;

// FR-06, UC-03〜05, 計画 ADR-0053 決定 3, #1172, IADR-0492 決定 6: 報告書の冒頭に書く「集計したセッション」の範囲。
// 範囲は約定を絞る窓（ReportSchedule.SessionWindowOf）と同じ窓から、市場ごとの現地取引日で引く。
//
// 暦の基準: 2026-10-09 は金曜、2026-10-12（月・スポーツの日）は東証の休場日（米国は開場）。米国の大引け 16:00 ET は
// 翌日 05:00 JST（夏時間）／ 06:00 JST（冬時間）。テスト ID は T-06-029〜（走査の結果、既存の T-06 帯は T-06-028 まで）。
public class ReportSessionRangeTests
{
    private static readonly ReportScheduleOptions Defaults = new();

    private static readonly ReportScheduleOptions JpHolidays = new()
    {
        Holidays = new HashSet<DateOnly>
        {
            new(2026, 10, 12), new(2026, 12, 31), new(2027, 1, 1), new(2027, 1, 11),
        },
    };

    private static readonly Market[] Both = [Market.UnitedStates, Market.Japan];

    private static IReadOnlyList<ReportSessionRange> RangesOf(
        ReportKind kind, DateOnly start, ReportScheduleOptions options, IReadOnlyCollection<Market>? markets = null) =>
        ReportSchedule.SessionRangesOf(ReportSchedule.PeriodOf(kind, start, options), options, markets ?? Both);

    private static string Line(IReadOnlyList<ReportSessionRange> ranges)
    {
        var view = new ReportView
        {
            Kind = ReportKind.Daily,
            PeriodKey = "daily-2026-10-13",
            PeriodLabel = "2026-10-13",
            Pnl = new PnlSummary(0m, 0m, 0m, 0m, 0m, 0, 0, 0),
            SessionRanges = ranges,
        };
        return ReportRenderer.RenderMarkdown(view).Split('\n').Single(l => l.StartsWith("集計したセッション: ", StringComparison.Ordinal));
    }

    // T-06-029, FR-06, 計画 ADR-0053 決定 3, #1172: 東証の休場日（月）の翌営業日の日報は、金曜と月曜の米国のセッションを数え、
    // 東証は当日だけを数える（休場日と週末を範囲の端に出さない）。米国だけを報告する構成では米国の行だけを書く。
    [Fact]
    public void T06_029_東証の休場日明けの日報は米国の2セッションを書き_米国だけの構成では米国だけを書く()
    {
        var ranges = RangesOf(ReportKind.Daily, new DateOnly(2026, 10, 13), JpHolidays);

        Line(ranges).Should().Be("集計したセッション: 米国 2026-10-09〜2026-10-12（ET）／東証 2026-10-13（JST）");

        var usOnly = RangesOf(ReportKind.Daily, new DateOnly(2026, 10, 13), JpHolidays, [Market.UnitedStates]);
        Line(usOnly).Should().Be("集計したセッション: 米国 2026-10-09〜2026-10-12（ET）");
    }

    // T-06-030, FR-06, 計画 ADR-0053 決定 2・3, #1172: 週報は前週金曜の米国のセッションから始まり、当週金曜の米国のセッションは
    // 次の週報に入る（範囲に出さない）。月曜の日報は金曜の米国のセッション 1 日だけを書く（週末を範囲に含めない）。
    [Fact]
    public void T06_030_週報は前週金曜の米国のセッションを含み当週金曜の米国のセッションを含まない()
    {
        Line(RangesOf(ReportKind.Weekly, new DateOnly(2026, 10, 5), Defaults))
            .Should().Be("集計したセッション: 米国 2026-10-02〜2026-10-08（ET）／東証 2026-10-05〜2026-10-09（JST）");

        Line(RangesOf(ReportKind.Daily, new DateOnly(2026, 10, 5), Defaults))
            .Should().Be("集計したセッション: 米国 2026-10-02（ET）／東証 2026-10-05（JST）");
        Line(RangesOf(ReportKind.Daily, new DateOnly(2026, 10, 6), Defaults))
            .Should().Be("集計したセッション: 米国 2026-10-05（ET）／東証 2026-10-06（JST）");
    }

    // T-06-031, FR-06, 計画 ADR-0053 決定 2・3, #1172: 年を跨ぐ月報。12 月の月報（最終営業日 12-30）は ET 12-30・12-31 の
    // 米国のセッションを数えず、1 月の月報がそれを数える。東証の年始の休場日（1/1）と週末は範囲の端に出さない。
    [Fact]
    public void T06_031_年末の米国のセッションは翌年1月の月報の範囲に入る()
    {
        Line(RangesOf(ReportKind.Monthly, new DateOnly(2026, 12, 1), JpHolidays))
            .Should().Be("集計したセッション: 米国 2026-11-30〜2026-12-29（ET）／東証 2026-12-01〜2026-12-30（JST）");
        Line(RangesOf(ReportKind.Monthly, new DateOnly(2027, 1, 1), JpHolidays))
            .Should().Be("集計したセッション: 米国 2026-12-30〜2027-01-28（ET）／東証 2027-01-04〜2027-01-29（JST）");
    }

    // T-06-032, FR-06, 計画 ADR-0053 決定 3, #1172: 窓にセッションが無い市場は「なし」と書く（行ごと落とさない）。
    // 営業日でない日の日報（手で作られた行）の窓は空であり、両市場とも「なし」になる。
    [Fact]
    public void T06_032_窓にセッションが無い市場はなしと書く()
    {
        var ranges = RangesOf(ReportKind.Daily, new DateOnly(2026, 10, 12), JpHolidays);

        ranges.Should().OnlyContain(r => !r.HasSession);
        Line(ranges).Should().Be("集計したセッション: 米国 なし／東証 なし");

        Line([new ReportSessionRange(Market.UnitedStates, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 5)),
              new ReportSessionRange(Market.Japan, new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 6))])
            .Should().Be("集計したセッション: 米国 2026-10-05（ET）／東証 なし");
    }

    // T-06-033, FR-06, 計画 ADR-0053 決定 3, #1172: 窓を持たない経路（手動の生成 API・SessionRanges が null）は行を出さない。
    // 行はタイトルの直下・§1 の前に置く（散文の節には入れない）。
    [Fact]
    public void T06_033_範囲はタイトル直下に置き_窓を持たない経路では出さない()
    {
        var view = new ReportView
        {
            Kind = ReportKind.Daily,
            PeriodKey = "daily-2026-10-06",
            PeriodLabel = "2026-10-06",
            Pnl = new PnlSummary(0m, 0m, 0m, 0m, 0m, 0, 0, 0),
            Narrative = "散文",
        };

        ReportRenderer.RenderMarkdown(view).Should().NotContain("集計したセッション");

        var body = ReportRenderer.RenderMarkdown(view with
        {
            SessionRanges = RangesOf(ReportKind.Daily, new DateOnly(2026, 10, 6), Defaults),
        });
        body.Should().Contain("# 日報 2026-10-06\n\n集計したセッション: 米国 2026-10-05（ET）／東証 2026-10-06（JST）\n\n## 1. 当日サマリ");
    }

    // T-06-034, FR-06, 計画 ADR-0053 決定 3, #1172: 書く市場は構成の対象市場から決める。解釈できる値が 1 つも無い
    // （既定の空を含む）なら全市場を書く（市場を黙って落とさない）。
    [Theory]
    [InlineData(new[] { "US" }, new[] { Market.UnitedStates })]
    [InlineData(new[] { "jp" }, new[] { Market.Japan })]
    [InlineData(new[] { "JP", "UnitedStates" }, new[] { Market.Japan, Market.UnitedStates })]
    [InlineData(new string[0], new[] { Market.Japan, Market.UnitedStates })]
    [InlineData(new[] { "XX", " " }, new[] { Market.Japan, Market.UnitedStates })]
    public void T06_034_書く市場は構成の対象市場から決める(string[] configured, Market[] expected)
    {
        ReportAutoGenerator.ReportedMarkets(configured).Should().BeEquivalentTo(expected);
    }

    // T-06-069, FR-06, #1224, IADR-0516 決定 5: 窓に揃えない入力（LLM 利用実績）は「集計したセッション」の行の末尾に JST の暦日の範囲を書く
    // （複数日なら `最初〜最後`）。範囲が無い（週報・null）なら書き足さない。
    [Fact]
    public void T06_069_窓に揃えないLLM利用実績は行の末尾に暦日の範囲を書く()
    {
        var ranges = RangesOf(ReportKind.Monthly, new DateOnly(2026, 10, 1), Defaults);
        var view = new ReportView
        {
            Kind = ReportKind.Monthly,
            PeriodKey = "monthly-2026-10",
            PeriodLabel = "2026-10",
            Pnl = new PnlSummary(0m, 0m, 0m, 0m, 0m, 0, 0, 0),
            SessionRanges = ranges,
            LlmUsageCalendarDays = new ReportCalendarDays(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 30)),
        };

        ReportRenderer.RenderMarkdown(view).Split('\n').Single(l => l.StartsWith("集計したセッション: ", StringComparison.Ordinal))
            .Should().Be("集計したセッション: 米国 2026-09-30〜2026-10-29（ET）／東証 2026-10-01〜2026-10-30（JST）・LLM 利用実績は JST の暦日 2026-10-01〜2026-10-30");
        Line(ranges).Should().Be("集計したセッション: 米国 2026-09-30〜2026-10-29（ET）／東証 2026-10-01〜2026-10-30（JST）");
    }
}
