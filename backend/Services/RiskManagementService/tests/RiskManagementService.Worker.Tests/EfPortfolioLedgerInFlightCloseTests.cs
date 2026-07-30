using AiStockTrading.RiskManagement.Worker.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiStockTrading.RiskManagement.Worker.Tests;

// FR-10, #292, IADR-0117: 処理中（承認済み・未約定）の決済数量。Application 側の
// PortfolioLedgerInFlightCloseTests（InMemory 実装）と同一の観点を EF 実装でも固定し、両実装の乖離を検知する。
public class EfPortfolioLedgerInFlightCloseTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Window = Now.AddMinutes(-30);

    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static Guid Approve(
        EfPortfolioLedgerStore store,
        PositionEffect effect,
        int quantity,
        DateTimeOffset approvedAt,
        string symbol = "AAPL",
        Market market = Market.UnitedStates)
    {
        var decisionId = Guid.NewGuid();
        store.AppendApproval(
            decisionId,
            new OrderIntent(symbol, market, TradeSide.Sell, ProductType.Cash, TradeMode.Paper,
                quantity, 21m, effect, StopLossPrice: null, FxRateToBase: 1m),
            approvedAt);
        return decisionId;
    }

    [Fact]
    public void 承認も約定も無ければゼロ()
    {
        using var db = NewContext(Guid.NewGuid().ToString());

        new EfPortfolioLedgerStore(db)
            .GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 未約定の決済承認を数える()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(60);
    }

    [Fact]
    public void 複数の未約定決済を合計する()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
        Approve(store, PositionEffect.Close, 15, Now.AddMinutes(-1));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(75);
    }

    [Fact]
    public void 部分約定は未約定ぶんだけを数える()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
        store.AppendFill(id, "ORD-1", 20, 21m, Now.AddMinutes(-4));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(40);
    }

    [Fact]
    public void 全量約定した決済は数えない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
        store.AppendFill(id, "ORD-1", 60, 21m, Now.AddMinutes(-4));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 約定が承認数量を超えても負にはしない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
        store.AppendFill(id, "ORD-1", 70, 21m, Now.AddMinutes(-4));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 新規建ての承認は数えない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        Approve(store, PositionEffect.Open, 60, Now.AddMinutes(-5));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 窓より前に承認された決済は数えない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-31));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 別銘柄と別市場は数えない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5), symbol: "MSFT");
        Approve(store, PositionEffect.Close, 30, Now.AddMinutes(-5), market: Market.Japan);

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 別コンテキストで読み直しても同じ結果になる()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            var store = new EfPortfolioLedgerStore(db);
            var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
            store.AppendFill(id, "ORD-1", 20, 21m, Now.AddMinutes(-4));
        }

        using var db2 = NewContext(dbName);
        new EfPortfolioLedgerStore(db2)
            .GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(40);
    }
}
