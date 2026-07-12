using FluentAssertions;
using Xunit;

namespace AiStockTrading.Report.Domain.Tests;

// FR-06, 04_report-templates, IADR-0032: 期間表記・自然キーの導出（純関数・種別ごとの形式）を検証する。
public class ReportPeriodTests
{
    private static readonly DateOnly Mon = new(2026, 7, 6); // ISO 週 2026-W28（月曜）

    [Theory]
    [InlineData(ReportKind.Daily, "2026-07-06")]
    [InlineData(ReportKind.Weekly, "2026-W28")]
    [InlineData(ReportKind.Monthly, "2026-07")]
    public void 種別ごとの期間表記を返す(ReportKind kind, string expected)
    {
        ReportPeriod.Label(kind, Mon).Should().Be(expected);
    }

    [Theory]
    [InlineData(ReportKind.Daily, "daily-2026-07-06")]
    [InlineData(ReportKind.Weekly, "weekly-2026-W28")]
    [InlineData(ReportKind.Monthly, "monthly-2026-07")]
    public void 種別ごとの自然キーを返す(ReportKind kind, string expected)
    {
        ReportPeriod.ExpectedKey(kind, Mon).Should().Be(expected);
    }

    [Fact]
    public void 年をまたぐ_ISO週は所属年で表記する()
    {
        // 2026-12-31（木）は ISO 週 2026-W53。年始のはみ出し週は前年 W へ寄る場合がある（ISOWeek 準拠）。
        ReportPeriod.Label(ReportKind.Weekly, new DateOnly(2026, 12, 31)).Should().Be("2026-W53");
    }
}
