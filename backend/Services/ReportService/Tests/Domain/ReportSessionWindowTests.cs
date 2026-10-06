using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using ReportService.Domain;
using Xunit;

namespace ReportService.Tests;

// FR-06, UC-03〜05, 04_workflows/03_reporting-cycle, #1172, IADR-0492: 報告書が集計するセッションの窓（純関数）。
// 生成は 16:00 JST のまま、窓は「前の営業日の生成境界の後〜期間の最終営業日の生成境界まで」に大引けを迎えたセッション。
//
// 暦の基準: 2026-10-05 は月曜（ISO 週 2026-W41）。米国の夏時間は 2026-03-08（日）に始まり 2026-11-01（日）に終わる。
// 米国の大引け 16:00 ET ＝ 翌日 05:00 JST（夏時間）／ 06:00 JST（冬時間）。東証の大引け 15:30 JST。
// テスト ID は T-06-015〜（走査の結果、既存の T-06 帯は T-06-014 まで）。
public class ReportSessionWindowTests
{
    private static readonly ReportScheduleOptions Defaults = new();

    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    private static DateTimeOffset Et(int year, int month, int day, int hour, int minute, int second = 0)
    {
        var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Eastern.GetUtcOffset(local));
    }

    private static DateTimeOffset Jst(int year, int month, int day, int hour, int minute) =>
        new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified), TimeSpan.FromHours(9));

    private static ReportSessionWindow WindowOf(ReportKind kind, DateOnly start, ReportScheduleOptions? options = null)
    {
        var o = options ?? Defaults;
        return ReportSchedule.SessionWindowOf(ReportSchedule.PeriodOf(kind, start, o), o);
    }

    private static ReportSessionWindow DailyWindow(int year, int month, int day, ReportScheduleOptions? options = null) =>
        WindowOf(ReportKind.Daily, new DateOnly(year, month, day), options);

    private static IEnumerable<DateOnly> Days(DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1))
            yield return d;
    }

    // 各市場のセッション中の 1 瞬間（米国 12:00 ET・東証 10:00 JST）。
    private static DateTimeOffset MidSession(Market market, DateOnly day) => market == Market.UnitedStates
        ? Et(day.Year, day.Month, day.Day, 12, 0)
        : Jst(day.Year, day.Month, day.Day, 10, 0);

    private static readonly DateOnly[] JpHolidays2026Autumn =
    [
        new(2026, 9, 21), new(2026, 9, 22), new(2026, 9, 23), new(2026, 10, 12), new(2026, 11, 3), new(2026, 11, 23),
    ];

    // T-06-015, FR-06, #1172（再現）: 2026-10-05（ET）の米国の約定（MSFT・NVDA の利確）は日報 2026-10-06（16:00 JST 生成）に入る。
    // 日報 2026-10-05（その日の 16:00 JST ＝ ET 03:00、セッション前）には入らない。修正前はどちらにも入らなかった。
    [Fact]
    public void T06_015_ET_10月5日の米国の約定は日報_10月6日に入り_10月5日には入らない()
    {
        var msftTakeProfit = Et(2026, 10, 5, 14, 0);
        var nvdaTakeProfit = Et(2026, 10, 5, 15, 30);

        var daily1006 = DailyWindow(2026, 10, 6);
        daily1006.Includes(Market.UnitedStates, msftTakeProfit).Should().BeTrue();
        daily1006.Includes(Market.UnitedStates, nvdaTakeProfit).Should().BeTrue();
        daily1006.TradingDays(Market.UnitedStates).Should().Be((new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 5)));

        var daily1005 = DailyWindow(2026, 10, 5);
        daily1005.Includes(Market.UnitedStates, msftTakeProfit).Should().BeFalse("16:00 JST の時点で ET 10-05 のセッションは始まってもいない");
    }

    // T-06-016, FR-06, #1172: 東証の約定は従来どおり同じ日付の日報に入る（16:00 JST は 15:30 の大引けの後）。
    [Fact]
    public void T06_016_東証の約定は従来どおり同じ日付の日報に入る()
    {
        foreach (var day in Days(new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31))
                     .Where(d => ReportSchedule.IsBusinessDay(d, Defaults)))
        {
            var window = DailyWindow(day.Year, day.Month, day.Day);
            window.Includes(Market.Japan, Jst(day.Year, day.Month, day.Day, 15, 29)).Should().BeTrue($"{day} の東証の約定");
            window.Includes(Market.Japan, Jst(day.Year, day.Month, day.Day, 9, 0)).Should().BeTrue();
        }

        // 火曜の日報の東証の取引日は火曜だけ（前日・翌日を含まない）。
        DailyWindow(2026, 10, 6).TradingDays(Market.Japan).Should().Be((new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 6)));
    }

    // T-06-017, FR-06, #1172（性質）: どの市場のどのセッションも、連続する日報のちょうど 1 つに入る（取りこぼしも二重計上も無い）。
    // 休場日の構成（週末のみ／東証の祝日を構成）と夏時間の切替（11-01）を跨いで確かめる。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void T06_017_各セッションは連続する日報のちょうど1つに入る(bool withJpHolidays)
    {
        var options = withJpHolidays ? Defaults with { Holidays = new HashSet<DateOnly>(JpHolidays2026Autumn) } : Defaults;
        var dailies = Days(new DateOnly(2026, 8, 25), new DateOnly(2027, 1, 15))
            .Where(d => ReportSchedule.IsBusinessDay(d, options))
            .Select(d => DailyWindow(d.Year, d.Month, d.Day, options))
            .ToList();

        foreach (var market in ReportSessionWindow.Markets)
        {
            foreach (var session in Days(new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31))
                         .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)))
            {
                dailies.Count(w => w.Includes(market, MidSession(market, session)))
                    .Should().Be(1, $"{market} {session} のセッションは日報 1 つにだけ入る");
            }
        }
    }

    // T-06-018, FR-06, #1172: 月曜の日報は金曜（ET）の米国のセッションを含む（JST 土曜 05:00〜06:00 に大引け）。
    [Fact]
    public void T06_018_月曜の日報は金曜の米国のセッションを含む()
    {
        var monday = DailyWindow(2026, 10, 12);

        monday.TradingDays(Market.UnitedStates).Should().Be((new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 11)));
        monday.Includes(Market.UnitedStates, Et(2026, 10, 9, 15, 59)).Should().BeTrue();
        DailyWindow(2026, 10, 9).Includes(Market.UnitedStates, Et(2026, 10, 9, 15, 59)).Should().BeFalse();
    }

    // T-06-019, FR-06, #1172: 東証の祝日と米国の祝日が食い違っても取りこぼさない。
    // (1) 構成した休場日（10-12・スポーツの日）には日報が無い。米国はその日も開いており、金曜と月曜（ET）のセッションは
    //     火曜の日報が両方とも数える。手で作られた休場日の日報の窓は空（二重に数えない）。
    // (2) 米国の祝日（11-26・感謝祭）は ET 11-26 のセッションが無いだけで、前後のセッション（半日取引の 11-27 を含む）は落ちない。
    [Fact]
    public void T06_019_東証と米国の祝日の食い違いで取りこぼさない()
    {
        var options = Defaults with { Holidays = new HashSet<DateOnly>([new DateOnly(2026, 10, 12)]) };

        ReportSchedule.Due(Jst(2026, 10, 12, 16, 0), options).Should().NotContain(d => d.PeriodKey == "daily-2026-10-12");
        var tuesday = DailyWindow(2026, 10, 13, options);
        tuesday.TradingDays(Market.UnitedStates).Should().Be((new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 12)));
        tuesday.Includes(Market.UnitedStates, Et(2026, 10, 9, 11, 0)).Should().BeTrue();
        tuesday.Includes(Market.UnitedStates, Et(2026, 10, 12, 11, 0)).Should().BeTrue();

        var holidayDaily = DailyWindow(2026, 10, 12, options);
        foreach (var market in ReportSessionWindow.Markets)
        {
            var (from, to) = holidayDaily.TradingDays(market);
            from.Should().BeAfter(to, $"休場日の日報は {market} のセッションを数えない");
        }

        DailyWindow(2026, 11, 26).Includes(Market.UnitedStates, Et(2026, 11, 25, 15, 0)).Should().BeTrue();
        DailyWindow(2026, 11, 27).TradingDays(Market.UnitedStates).Should().Be((new DateOnly(2026, 11, 26), new DateOnly(2026, 11, 26)));
        DailyWindow(2026, 11, 30).Includes(Market.UnitedStates, Et(2026, 11, 27, 12, 30)).Should().BeTrue("半日取引の金曜は月曜の日報");
    }

    // T-06-020, FR-06, #1172: 夏時間の切替（3 月・11 月）を跨いでも、大引け前の最後の約定と大引け後に記録された約定は
    // その ET 取引日のセッションとして次の営業日の日報に入る（固定オフセットで換算しない）。
    [Theory]
    [InlineData(2026, 3, 6, 2026, 3, 9)]    // 金曜（冬時間・JST 土 06:00 大引け）→ 月曜の日報
    [InlineData(2026, 3, 9, 2026, 3, 10)]   // 夏時間の初日の月曜（JST 火 05:00 大引け）→ 火曜の日報
    [InlineData(2026, 10, 30, 2026, 11, 2)] // 夏時間の最後の金曜 → 月曜の日報
    [InlineData(2026, 11, 2, 2026, 11, 3)]  // 冬時間の初日の月曜（JST 火 06:00 大引け）→ 火曜の日報
    public void T06_020_夏時間の切替を跨いでも米国のセッションは次の営業日の日報に入る(
        int sy, int sm, int sd, int dy, int dm, int dd)
    {
        var lastBeforeClose = Et(sy, sm, sd, 15, 59, 59);
        var reportedAfterClose = Et(sy, sm, sd, 16, 0, 30);
        var expected = DailyWindow(dy, dm, dd);

        expected.Includes(Market.UnitedStates, lastBeforeClose).Should().BeTrue();
        expected.Includes(Market.UnitedStates, reportedAfterClose).Should().BeTrue();

        var previous = new DateOnly(dy, dm, dd).AddDays(-1);
        while (!ReportSchedule.IsBusinessDay(previous, Defaults))
            previous = previous.AddDays(-1);
        DailyWindow(previous.Year, previous.Month, previous.Day).Includes(Market.UnitedStates, lastBeforeClose).Should().BeFalse();
    }

    // T-06-021, FR-06, #1172: 週報・月報の窓はその期間の日報の窓の和に等しい。最終営業日（金曜・月末）の米国のセッションは
    // 生成時点でまだ閉じていないため次の週報・月報に入り、どの週報・月報にもちょうど 1 回だけ入る。
    [Fact]
    public void T06_021_週報と月報は最終セッションをちょうど1回だけ数え日報の和に等しい()
    {
        var w41 = WindowOf(ReportKind.Weekly, new DateOnly(2026, 10, 5));
        w41.ClosedAfter.Should().Be(DailyWindow(2026, 10, 5).ClosedAfter);
        w41.ClosedUntil.Should().Be(DailyWindow(2026, 10, 9).ClosedUntil);
        w41.TradingDays(Market.UnitedStates).Should().Be((new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 8)));
        w41.TradingDays(Market.Japan).Should().Be((new DateOnly(2026, 10, 3), new DateOnly(2026, 10, 9)));

        var october = WindowOf(ReportKind.Monthly, new DateOnly(2026, 10, 1));
        october.TradingDays(Market.UnitedStates).Should().Be((new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 29)));
        october.TradingDays(Market.Japan).Should().Be((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 30)));

        var weeklies = Days(new DateOnly(2026, 8, 31), new DateOnly(2027, 1, 4))
            .Where(d => d.DayOfWeek == DayOfWeek.Monday)
            .Select(d => WindowOf(ReportKind.Weekly, d))
            .ToList();
        var monthlies = Enumerable.Range(8, 6)
            .Select(m => WindowOf(ReportKind.Monthly, new DateOnly(2026 + ((m - 1) / 12), ((m - 1) % 12) + 1, 1)))
            .ToList();

        foreach (var market in ReportSessionWindow.Markets)
        {
            foreach (var session in Days(new DateOnly(2026, 9, 7), new DateOnly(2026, 12, 25))
                         .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)))
            {
                var at = MidSession(market, session);
                weeklies.Count(w => w.Includes(market, at)).Should().Be(1, $"{market} {session} は週報 1 つにだけ入る");
                monthlies.Count(w => w.Includes(market, at)).Should().Be(1, $"{market} {session} は月報 1 つにだけ入る");
            }
        }

        // 10 月の最終営業日（10-30 金）の米国のセッションは 11 月の月報が数える（10 月の月報の生成時点では始まっていない）。
        october.Includes(Market.UnitedStates, Et(2026, 10, 30, 12, 0)).Should().BeFalse();
        WindowOf(ReportKind.Monthly, new DateOnly(2026, 11, 1)).Includes(Market.UnitedStates, Et(2026, 10, 30, 12, 0)).Should().BeTrue();
    }

    // T-06-022, FR-06, #1172: 照会の範囲は全市場の取引日の外包（契約〔市場の現地取引日の [from, to]〕は変えない）。
    [Fact]
    public void T06_022_照会の範囲は全市場の取引日の外包である()
    {
        DailyWindow(2026, 10, 6).QueryRange().Should().Be((new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 6)));
        DailyWindow(2026, 10, 12).QueryRange().Should().Be((new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 12)));
    }
}
