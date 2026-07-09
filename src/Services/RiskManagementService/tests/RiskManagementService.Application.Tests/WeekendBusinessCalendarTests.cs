using AiStockTrading.RiskManagement.Application.Adapters;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-10: 週末スキップの営業日算出。ロックアウト解除日（翌営業日）の基礎。祝日対応は #21 で差し替える。
public class WeekendBusinessCalendarTests
{
    private readonly WeekendBusinessCalendar _calendar = new();

    [Fact]
    public void 平日の翌営業日は翌日()
    {
        // 2026-07-09 木曜 → 2026-07-10 金曜
        _calendar.NextBusinessDay(new DateOnly(2026, 7, 9)).Should().Be(new DateOnly(2026, 7, 10));
    }

    [Fact]
    public void 金曜の翌営業日は月曜()
    {
        // 2026-07-10 金曜 → 2026-07-13 月曜（土日をスキップ）
        _calendar.NextBusinessDay(new DateOnly(2026, 7, 10)).Should().Be(new DateOnly(2026, 7, 13));
    }

    [Fact]
    public void 土曜の翌営業日は月曜()
    {
        _calendar.NextBusinessDay(new DateOnly(2026, 7, 11)).Should().Be(new DateOnly(2026, 7, 13));
    }
}
