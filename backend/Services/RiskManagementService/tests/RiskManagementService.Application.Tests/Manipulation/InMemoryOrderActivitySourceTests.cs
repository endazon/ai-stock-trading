using RiskManagementService.Application.Adapters;
using RiskManagementService.Domain.Manipulation;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Application.Tests.Manipulation;

// FR-19, IADR-0040: 注文アクティビティ源（インメモリ）の窓抽出・窓外刈り込み・（銘柄, 市場）分離を固定する。
public class InMemoryOrderActivitySourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 9, 35, 0, TimeSpan.Zero);

    private static OrderActivityRecord Record(DateTimeOffset placedAt) => new()
    {
        PlacedAt = placedAt,
        Side = TradeSide.Buy,
        Quantity = 10,
        FilledQuantity = 0,
        Status = OrderStatus.Cancelled,
        TerminalAt = placedAt.AddSeconds(1),
    };

    [Fact]
    public void 窓内の記録だけを返す()
    {
        var source = new InMemoryOrderActivitySource();
        source.Record("AAPL", Market.UnitedStates, Record(Now.AddMinutes(-2))); // 窓内（5 分）
        source.Record("AAPL", Market.UnitedStates, Record(Now.AddMinutes(-10))); // 窓外

        var window = source.GetRecentActivity("AAPL", Market.UnitedStates, Now, TimeSpan.FromMinutes(5));

        window.Records.Should().HaveCount(1);
        window.Records[0].PlacedAt.Should().Be(Now.AddMinutes(-2));
    }

    [Fact]
    public void 記録のない銘柄は空窓を返す()
    {
        var source = new InMemoryOrderActivitySource();

        var window = source.GetRecentActivity("MSFT", Market.UnitedStates, Now, TimeSpan.FromMinutes(5));

        window.Records.Should().BeEmpty();
        window.Symbol.Should().Be("MSFT");
        window.AsOf.Should().Be(Now);
    }

    [Fact]
    public void 銘柄と市場で記録を分離する()
    {
        var source = new InMemoryOrderActivitySource();
        source.Record("AAPL", Market.UnitedStates, Record(Now.AddMinutes(-1)));
        source.Record("AAPL", Market.Japan, Record(Now.AddMinutes(-1)));

        var us = source.GetRecentActivity("AAPL", Market.UnitedStates, Now, TimeSpan.FromMinutes(5));
        var jp = source.GetRecentActivity("AAPL", Market.Japan, Now, TimeSpan.FromMinutes(5));

        us.Records.Should().HaveCount(1);
        jp.Records.Should().HaveCount(1);
        us.Market.Should().Be(Market.UnitedStates);
        jp.Market.Should().Be(Market.Japan);
    }
}
