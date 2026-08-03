using AiStockTrading.Report.Infrastructure.Composable.Polling;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Report.Infrastructure.Tests;

// FR-06, IADR-0115 決定6, #280: 自動生成の構成解釈を検証する。既定は安全側で、不正値は黙って壊れず既定へ倒す
// （閉場前に生成する・巡回が暴走する・休場日の解釈が消える、といった事故を避ける）。
public class ReportAutoGenerationOptionsTests
{
    [Fact]
    public void 既定は無効で東証の大引け後の境界を持つ()
    {
        var options = new ReportAutoGenerationOptions();

        options.Enabled.Should().BeFalse();
        options.Interval.Should().Be(TimeSpan.FromMinutes(5));

        var schedule = options.ToSettings().Schedule;
        schedule.DailyAt.Should().Be(new TimeOnly(16, 0));
        schedule.WeeklyAt.Should().Be(new TimeOnly(16, 30));
        schedule.MonthlyAt.Should().Be(new TimeOnly(17, 0));
        schedule.Holidays.Should().BeEmpty();
    }

    [Fact]
    public void 境界時刻と休場日を構成から取り込む()
    {
        var settings = new ReportAutoGenerationOptions
        {
            DailyAtJst = "18:15",
            WeeklyAtJst = "19:00",
            MonthlyAtJst = "20:30",
            Holidays = ["2026-07-20", "2026-08-11"],
        }.ToSettings();

        settings.Schedule.DailyAt.Should().Be(new TimeOnly(18, 15));
        settings.Schedule.WeeklyAt.Should().Be(new TimeOnly(19, 0));
        settings.Schedule.MonthlyAt.Should().Be(new TimeOnly(20, 30));
        settings.Schedule.Holidays.Should().BeEquivalentTo([new DateOnly(2026, 7, 20), new DateOnly(2026, 8, 11)]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("25:99")]
    [InlineData("夕方")]
    public void 解釈できない境界時刻は既定へ倒す(string value)
    {
        new ReportAutoGenerationOptions { DailyAtJst = value }.ToSettings()
            .Schedule.DailyAt.Should().Be(new TimeOnly(16, 0));
    }

    [Fact]
    public void 解釈できない休場日は無視して残りを採る()
    {
        var holidays = new ReportAutoGenerationOptions { Holidays = ["2026-07-20", "not-a-date", ""] }
            .ToSettings().Schedule.Holidays;

        holidays.Should().BeEquivalentTo([new DateOnly(2026, 7, 20)]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 非正の巡回間隔は既定へ倒す(int seconds)
    {
        // 0 秒や負値をそのまま PeriodicTimer に渡すと暴走ループ・例外になる。
        new ReportAutoGenerationOptions { IntervalSeconds = seconds }.Interval.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(7, 7)]
    public void 前提条件バージョンは非正値なら_1_へ倒す(int configured, int expected)
    {
        new ReportAutoGenerationOptions { AssumptionsVersion = configured }.ToSettings()
            .AssumptionsVersion.Should().Be(expected);
    }

    [Fact]
    public void 空白のみの市場表記は落とす()
    {
        new ReportAutoGenerationOptions { Markets = ["JP", " ", "US", ""] }.ToSettings()
            .Markets.Should().BeEquivalentTo(["JP", "US"]);
    }

    [Fact]
    public void 提示の操作者は既定でスケジューラを表す()
    {
        // 確定（Confirm）は利用者のみ。提示（Present）の actor が利用者名で残らないようにする（ADR-0003）。
        new ReportAutoGenerationOptions().ToSettings().Actor.Should().Be("report-scheduler");
    }
}
