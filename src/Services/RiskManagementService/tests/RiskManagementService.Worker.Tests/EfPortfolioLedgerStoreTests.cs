using AiStockTrading.RiskManagement.Worker.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiStockTrading.RiskManagement.Worker.Tests;

// FR-10, FR-05, IADR-0018: 取引台帳 EF ストアの永続化・相関・冪等を InMemory DB で検証する。
public class EfPortfolioLedgerStoreTests
{
    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static OrderIntent BuyIntent(int qty, decimal price) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, qty, price);

    [Fact]
    public void 承認と約定を記録すると相関済みの約定を返す()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            var store = new EfPortfolioLedgerStore(db);
            store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);
            store.AppendFill(decisionId, "ORD-1", 10, 1_050m, DateTimeOffset.UtcNow).Should().BeTrue();
        }

        using var db2 = NewContext(dbName);
        var fills = new EfPortfolioLedgerStore(db2).GetFills();
        fills.Should().HaveCount(1);
        fills[0].Symbol.Should().Be("AAPL");
        fills[0].Side.Should().Be(TradeSide.Buy);
        fills[0].PositionEffect.Should().Be(PositionEffect.Open);
        fills[0].Quantity.Should().Be(10);
        fills[0].Price.Should().Be(1_050m);
    }

    [Fact]
    public void 承認のない約定は記録されず_false_を返す()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);

        store.AppendFill(Guid.NewGuid(), "ORD-X", 10, 1_000m, DateTimeOffset.UtcNow).Should().BeFalse();
        store.GetFills().Should().BeEmpty();
    }

    [Fact]
    public void 同一_OrderId_の再送は重複記録しない()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var decisionId = Guid.NewGuid();
        store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);

        store.AppendFill(decisionId, "ORD-1", 10, 1_000m, DateTimeOffset.UtcNow).Should().BeTrue();
        store.AppendFill(decisionId, "ORD-1", 10, 1_000m, DateTimeOffset.UtcNow).Should().BeTrue(); // 再送

        store.GetFills().Should().HaveCount(1);
    }

    [Fact]
    public void 同一_DecisionId_の承認再送は最初の内容を保持する()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var decisionId = Guid.NewGuid();

        store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);
        store.AppendApproval(decisionId, BuyIntent(999, 9_999m), DateTimeOffset.UtcNow); // 再送（無視）
        store.AppendFill(decisionId, "ORD-1", 10, 1_000m, DateTimeOffset.UtcNow);

        var fills = store.GetFills();
        fills.Should().HaveCount(1);
        fills[0].Quantity.Should().Be(10);
    }
}
