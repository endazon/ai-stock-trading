using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-06, FR-16, IADR-0115 決定5, #280: 報告書サービスへ供給する期間約定の絞り込み（純関数）を検証する。
// 取引日は JST 境界（PortfolioProjection.TradeDate）で解釈する。UTC 暦日で切ると日本市場の午前が前日に落ちる。
public class PeriodFillQueryTests
{
    private static LedgerFill At(DateTimeOffset executedAt, string symbol = "7203") =>
        new(symbol, Market.Japan, TradeSide.Buy, PositionEffect.Open, 100, 2_000m, executedAt);

    [Fact]
    public void 期間内の約定だけを返す()
    {
        var fills = new[]
        {
            At(new DateTimeOffset(2026, 7, 6, 1, 0, 0, TimeSpan.Zero), "before"),  // JST 07-06
            At(new DateTimeOffset(2026, 7, 7, 1, 0, 0, TimeSpan.Zero), "inside"),  // JST 07-07
            At(new DateTimeOffset(2026, 7, 10, 1, 0, 0, TimeSpan.Zero), "last"),   // JST 07-10
            At(new DateTimeOffset(2026, 7, 13, 1, 0, 0, TimeSpan.Zero), "after"),  // JST 07-13
        };

        var result = PeriodFillQuery.InTradingDayRange(fills, new DateOnly(2026, 7, 7), new DateOnly(2026, 7, 10));

        result.Select(f => f.Symbol).Should().BeEquivalentTo(["inside", "last"]);
    }

    [Fact]
    public void 取引日は_JST_境界で判定する()
    {
        // 2026-07-07T00:30:00Z ＝ JST 07-07 09:30（東証の寄り付き直後）。UTC 暦日でも 07-07 だが、
        // 2026-07-06T23:30:00Z ＝ JST 07-07 08:30 は UTC 暦日では 07-06 に落ちる。JST で 07-07 として拾う。
        var fills = new[] { At(new DateTimeOffset(2026, 7, 6, 23, 30, 0, TimeSpan.Zero), "jst-morning") };

        PeriodFillQuery.InTradingDayRange(fills, new DateOnly(2026, 7, 7), new DateOnly(2026, 7, 7))
            .Should().ContainSingle().Which.Symbol.Should().Be("jst-morning");
        PeriodFillQuery.InTradingDayRange(fills, new DateOnly(2026, 7, 6), new DateOnly(2026, 7, 6))
            .Should().BeEmpty();
    }

    [Fact]
    public void 境界日は両端とも含む()
    {
        var fills = new[]
        {
            At(new DateTimeOffset(2026, 7, 7, 1, 0, 0, TimeSpan.Zero), "from"),
            At(new DateTimeOffset(2026, 7, 10, 1, 0, 0, TimeSpan.Zero), "to"),
        };

        PeriodFillQuery.InTradingDayRange(fills, new DateOnly(2026, 7, 7), new DateOnly(2026, 7, 10))
            .Should().HaveCount(2);
    }

    [Fact]
    public void 約定時刻の昇順で返す()
    {
        var fills = new[]
        {
            At(new DateTimeOffset(2026, 7, 9, 1, 0, 0, TimeSpan.Zero), "later"),
            At(new DateTimeOffset(2026, 7, 7, 1, 0, 0, TimeSpan.Zero), "earlier"),
        };

        PeriodFillQuery.InTradingDayRange(fills, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31))
            .Select(f => f.Symbol).Should().ContainInOrder("earlier", "later");
    }

    [Fact]
    public void 逆順の期間は空を返す()
    {
        var fills = new[] { At(new DateTimeOffset(2026, 7, 7, 1, 0, 0, TimeSpan.Zero)) };

        PeriodFillQuery.InTradingDayRange(fills, new DateOnly(2026, 7, 10), new DateOnly(2026, 7, 7))
            .Should().BeEmpty();
    }
}
