using AiStockTrading.OrderExecution.Domain;
using AiStockTrading.OrderExecution.Worker.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiStockTrading.OrderExecution.Worker.Tests;

// FR-05, FR-16: 発注結果ストアの永続化を InMemory DB で検証する（ラウンドトリップ・新しい順）。
public class EfExecutedOrderStoreTests
{
    private static OrderExecutionDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<OrderExecutionDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static ExecutionRecord Record(string orderId, decimal slippage, DateTimeOffset at) =>
        new(Guid.NewGuid(), orderId, "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, 10, 1_000m, 10, 1_005m, OrderStatus.Filled, slippage, at);

    [Fact]
    public void 発注結果はラウンドトリップし別コンテキストからも読める()
    {
        var dbName = Guid.NewGuid().ToString();
        var rec = Record("o1", 0.005m, DateTimeOffset.UtcNow);

        using (var db = NewContext(dbName))
        {
            new EfExecutedOrderStore(db).Save(rec);
        }

        using var db2 = NewContext(dbName);
        var reloaded = new EfExecutedOrderStore(db2).GetAll().Should().ContainSingle().Subject;
        reloaded.OrderId.Should().Be("o1");
        reloaded.SlippageRatio.Should().Be(0.005m);
        reloaded.DecisionId.Should().Be(rec.DecisionId);
    }

    [Fact]
    public void 発注結果は新しい順で返る()
    {
        var dbName = Guid.NewGuid().ToString();
        var t0 = DateTimeOffset.UtcNow;
        using (var db = NewContext(dbName))
        {
            var store = new EfExecutedOrderStore(db);
            store.Save(Record("old", 0m, t0));
            store.Save(Record("new", 0m, t0.AddMinutes(1)));
        }

        using var db2 = NewContext(dbName);
        new EfExecutedOrderStore(db2).GetAll()[0].OrderId.Should().Be("new");
    }
}
