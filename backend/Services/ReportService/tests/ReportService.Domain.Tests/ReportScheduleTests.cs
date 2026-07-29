using FluentAssertions;
using Xunit;

namespace AiStockTrading.Report.Domain.Tests;

// FR-06, UC-03〜05, 04_report-templates, 03_reporting-cycle, IADR-0115, #280:
// 自動生成の期間判定（純関数）を検証する。時刻境界は JST 固定（PortfolioProjection.TradingDayOffset=+9 と整合）、
// 営業日は「土日でも構成休場日でもない日」。日報は閉場境界を過ぎた直近営業日 1 件、週報/月報は当週/当月の最終営業日基準。
//
// 暦の基準（既存 ReportPeriodTests と共有）: 2026-07-06 は月曜・ISO 週 2026-W28。
// したがって 07-10 は金曜、07-11 は土曜、07-12 は日曜。2026-07-27 月曜からが W31 で、07-31 は金曜＝7 月の最終営業日。
public class ReportScheduleTests
{
    private static readonly ReportScheduleOptions Defaults = new();

    // JST の壁時計を UTC の瞬間へ写す（テストの意図を「JST で何時か」で書けるようにする）。
    private static DateTimeOffset Jst(int year, int month, int day, int hour, int minute) =>
        new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified), TimeSpan.FromHours(9));

    private static ReportScheduleOptions WithHolidays(params DateOnly[] holidays) =>
        Defaults with { Holidays = new HashSet<DateOnly>(holidays) };

    private static DueReport? Of(IReadOnlyList<DueReport> due, ReportKind kind) =>
        due.SingleOrDefault(d => d.Kind == kind);

    // ---- 日報: 閉場境界（既定 16:00 JST） ----

    [Fact]
    public void 当日の閉場境界前は_当日ではなく直前の営業日が日報の対象になる()
    {
        // 水曜 15:59 JST。当日はまだ閉場していないため、対象は前営業日（火曜 07-07）。
        var due = ReportSchedule.Due(Jst(2026, 7, 8, 15, 59), Defaults);

        Of(due, ReportKind.Daily)!.PeriodKey.Should().Be("daily-2026-07-07");
    }

    [Fact]
    public void 当日の閉場境界に達したら_当日が日報の対象になる()
    {
        var due = ReportSchedule.Due(Jst(2026, 7, 8, 16, 0), Defaults);

        var daily = Of(due, ReportKind.Daily)!;
        daily.PeriodKey.Should().Be("daily-2026-07-08");
        daily.PeriodStart.Should().Be(new DateOnly(2026, 7, 8));
        daily.PeriodEnd.Should().Be(new DateOnly(2026, 7, 8));
    }

    [Fact]
    public void 境界時刻は_UTC_の瞬間から_JST_へ換算して判定する()
    {
        // 2026-07-08T07:00:00Z ＝ 16:00 JST（境界ちょうど）。UTC 暦日で判定していると 07:00 は境界前になり落ちる。
        var due = ReportSchedule.Due(new DateTimeOffset(2026, 7, 8, 7, 0, 0, TimeSpan.Zero), Defaults);

        Of(due, ReportKind.Daily)!.PeriodKey.Should().Be("daily-2026-07-08");
    }

    [Fact]
    public void 週末は_直前の営業日である金曜が日報の対象になる()
    {
        // 土曜 10:00 JST。土曜は営業日でないため、閉場済みの直近営業日（金曜 07-10）を対象にする。
        var due = ReportSchedule.Due(Jst(2026, 7, 11, 10, 0), Defaults);

        Of(due, ReportKind.Daily)!.PeriodKey.Should().Be("daily-2026-07-10");
    }

    [Fact]
    public void 月曜の朝は_前週金曜の日報が対象になる_夜間再起動で取りこぼさない()
    {
        // 確定した日報が翌営業日の取引方針になるため、前営業日ぶんは朝の再起動でも回収する（IADR-0115 決定2）。
        var due = ReportSchedule.Due(Jst(2026, 7, 13, 9, 0), Defaults);

        Of(due, ReportKind.Daily)!.PeriodKey.Should().Be("daily-2026-07-10");
    }

    [Fact]
    public void 構成された休場日は日報の対象にならない()
    {
        // 金曜 07-10 を休場日にすると、金曜 16:00 の時点の対象は木曜 07-09 になる。
        var due = ReportSchedule.Due(Jst(2026, 7, 10, 16, 0), WithHolidays(new DateOnly(2026, 7, 10)));

        Of(due, ReportKind.Daily)!.PeriodKey.Should().Be("daily-2026-07-09");
    }

    [Fact]
    public void 直近_7日がすべて非営業日なら日報の対象は無い()
    {
        var holidays = Enumerable.Range(0, 8).Select(i => new DateOnly(2026, 7, 8).AddDays(-i)).ToArray();

        var due = ReportSchedule.Due(Jst(2026, 7, 8, 16, 0), WithHolidays(holidays));

        Of(due, ReportKind.Daily).Should().BeNull();
    }

    // ---- 週報: 当 ISO 週の最終営業日（既定 16:30 JST） ----

    [Fact]
    public void 週の最終営業日でも境界時刻前は週報の対象にならない()
    {
        var due = ReportSchedule.Due(Jst(2026, 7, 10, 16, 0), Defaults);

        Of(due, ReportKind.Weekly).Should().BeNull();
        Of(due, ReportKind.Daily).Should().NotBeNull(); // 日報（16:00 境界）は既に対象
    }

    [Fact]
    public void 週の最終営業日の境界時刻に達したら週報の対象になる()
    {
        var due = ReportSchedule.Due(Jst(2026, 7, 10, 16, 30), Defaults);

        var weekly = Of(due, ReportKind.Weekly)!;
        weekly.PeriodKey.Should().Be("weekly-2026-W28");
        weekly.PeriodStart.Should().Be(new DateOnly(2026, 7, 6));  // 当週の月曜
        weekly.PeriodEnd.Should().Be(new DateOnly(2026, 7, 10));   // 当週の最終営業日
    }

    [Fact]
    public void 週末に入っても当週の週報は対象のままになる()
    {
        // 日曜 12:00 JST。最終営業日（金曜）の境界は既に過ぎており、まだ同じ ISO 週の中にいる。
        var due = ReportSchedule.Due(Jst(2026, 7, 12, 12, 0), Defaults);

        Of(due, ReportKind.Weekly)!.PeriodKey.Should().Be("weekly-2026-W28");
    }

    [Fact]
    public void 週の途中では週報の対象にならない()
    {
        var due = ReportSchedule.Due(Jst(2026, 7, 8, 23, 0), Defaults);

        Of(due, ReportKind.Weekly).Should().BeNull();
    }

    [Fact]
    public void 金曜が休場なら木曜が週の最終営業日になる()
    {
        var options = WithHolidays(new DateOnly(2026, 7, 10));

        // 金曜 09:00 JST。木曜（前日）の境界は既に過ぎているため、当週の週報が対象になる。
        var due = ReportSchedule.Due(Jst(2026, 7, 10, 9, 0), options);

        var weekly = Of(due, ReportKind.Weekly)!;
        weekly.PeriodKey.Should().Be("weekly-2026-W28");
        weekly.PeriodEnd.Should().Be(new DateOnly(2026, 7, 9));
    }

    [Fact]
    public void 週内に営業日が一つも無ければ週報の対象は無い()
    {
        var holidays = Enumerable.Range(0, 7).Select(i => new DateOnly(2026, 7, 6).AddDays(i)).ToArray();

        var due = ReportSchedule.Due(Jst(2026, 7, 12, 23, 0), WithHolidays(holidays));

        Of(due, ReportKind.Weekly).Should().BeNull();
    }

    // ---- 月報: 当月の最終営業日（既定 17:00 JST） ----

    [Fact]
    public void 月の最終営業日でも境界時刻前は月報の対象にならない()
    {
        // 2026-07-31 は金曜＝7 月の最終営業日。16:45 は週報境界（16:30）は超えるが月報境界（17:00）には届かない。
        var due = ReportSchedule.Due(Jst(2026, 7, 31, 16, 45), Defaults);

        Of(due, ReportKind.Monthly).Should().BeNull();
        Of(due, ReportKind.Weekly).Should().NotBeNull();
    }

    [Fact]
    public void 月末最終営業日の境界時刻に達したら_日報週報月報がすべて対象になる()
    {
        var due = ReportSchedule.Due(Jst(2026, 7, 31, 17, 0), Defaults);

        Of(due, ReportKind.Daily)!.PeriodKey.Should().Be("daily-2026-07-31");
        Of(due, ReportKind.Weekly)!.PeriodKey.Should().Be("weekly-2026-W31");

        var monthly = Of(due, ReportKind.Monthly)!;
        monthly.PeriodKey.Should().Be("monthly-2026-07");
        monthly.PeriodStart.Should().Be(new DateOnly(2026, 7, 1));
        monthly.PeriodEnd.Should().Be(new DateOnly(2026, 7, 31));
    }

    [Fact]
    public void 月の途中では月報の対象にならない()
    {
        var due = ReportSchedule.Due(Jst(2026, 7, 8, 23, 0), Defaults);

        Of(due, ReportKind.Monthly).Should().BeNull();
    }

    [Fact]
    public void 月末が週末なら直前の営業日が最終営業日になる()
    {
        // 2026-05-31 は日曜。5 月の最終営業日は 05-29（金）。日曜 23:00 の時点で境界は過ぎている。
        var due = ReportSchedule.Due(Jst(2026, 5, 31, 23, 0), Defaults);

        var monthly = Of(due, ReportKind.Monthly)!;
        monthly.PeriodKey.Should().Be("monthly-2026-05");
        monthly.PeriodEnd.Should().Be(new DateOnly(2026, 5, 29));
    }

    [Fact]
    public void 生成境界は構成で変更できる()
    {
        var options = Defaults with { DailyAt = new TimeOnly(18, 0) };

        ReportSchedule.Due(Jst(2026, 7, 8, 17, 0), options).Should()
            .NotContain(d => d.Kind == ReportKind.Daily && d.PeriodKey == "daily-2026-07-08");
        ReportSchedule.Due(Jst(2026, 7, 8, 18, 0), options).Should()
            .Contain(d => d.Kind == ReportKind.Daily && d.PeriodKey == "daily-2026-07-08");
    }
}
