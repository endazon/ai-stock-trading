using AiStockTrading.TradeDecision.Worker.Composable.Adapters;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.TradeDecision.Worker.Tests;

// FR-02, IADR-0023: 市場カレンダー（週末・構成休場日・市場ローカル TZ で取引日判定）を検証する。
public class MarketCalendarTests
{
    private static MarketCalendar Calendar(IReadOnlyDictionary<Market, IReadOnlySet<DateOnly>>? holidays = null) =>
        new(holidays ?? new Dictionary<Market, IReadOnlySet<DateOnly>>());

    // JST（+9）での特定日時。
    private static DateTimeOffset Jst(int y, int m, int d, int hour = 12) =>
        new(y, m, d, hour, 0, 0, TimeSpan.FromHours(9));

    // US Eastern（夏時間 EDT=-4）での特定日時。
    private static DateTimeOffset Edt(int y, int m, int d, int hour = 12) =>
        new(y, m, d, hour, 0, 0, TimeSpan.FromHours(-4));

    [Fact]
    public void 平日は開場()
    {
        // 2026-07-13 は月曜。
        Calendar().IsOpen(Market.Japan, Jst(2026, 7, 13)).Should().BeTrue();
    }

    [Fact]
    public void 週末は非開場()
    {
        // 2026-07-11 土 / 2026-07-12 日。
        Calendar().IsOpen(Market.Japan, Jst(2026, 7, 11)).Should().BeFalse();
        Calendar().IsOpen(Market.Japan, Jst(2026, 7, 12)).Should().BeFalse();
    }

    [Fact]
    public void 構成された休場日は非開場()
    {
        var holidays = new Dictionary<Market, IReadOnlySet<DateOnly>>
        {
            [Market.Japan] = new HashSet<DateOnly> { new(2026, 7, 13) },
        };

        // 月曜だが休場日に設定 → 非開場。
        Calendar(holidays).IsOpen(Market.Japan, Jst(2026, 7, 13)).Should().BeFalse();
        // 別市場（米国）は影響を受けない（US Eastern 月曜正午 → 開場）。
        Calendar(holidays).IsOpen(Market.UnitedStates, Edt(2026, 7, 13)).Should().BeTrue();
    }
}
