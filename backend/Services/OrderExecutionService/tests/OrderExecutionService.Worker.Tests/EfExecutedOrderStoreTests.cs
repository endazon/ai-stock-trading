using AiStockTrading.OrderExecution.Domain;
using AiStockTrading.OrderExecution.Worker.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
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

    // #270, IADR-0113: 約定追跡の対象抽出（非終端・追跡上限内・古い順・件数上限）。
    [Fact]
    public void 追跡対象は非終端かつ追跡上限内の記録だけを古い順に返す()
    {
        var dbName = Guid.NewGuid().ToString();
        var now = new DateTimeOffset(2026, 7, 29, 6, 0, 0, TimeSpan.Zero);
        using (var db = NewContext(dbName))
        {
            var store = new EfExecutedOrderStore(db);
            store.Save(Record("filled", 0m, now.AddMinutes(-5)) with { Status = OrderStatus.Filled });
            store.Save(Record("rejected", 0m, now.AddMinutes(-5)) with { Status = OrderStatus.Rejected });
            store.Save(Record("cancelled", 0m, now.AddMinutes(-5)) with { Status = OrderStatus.Cancelled });
            store.Save(Record("stale", 0m, now.AddHours(-30)) with { Status = OrderStatus.Accepted });
            store.Save(Record("accepted", 0m, now.AddMinutes(-3)) with { Status = OrderStatus.Accepted });
            store.Save(Record("partial", 0m, now.AddMinutes(-4)) with { Status = OrderStatus.PartiallyFilled });
        }

        using var db2 = NewContext(dbName);
        var pending = new EfExecutedOrderStore(db2).FindPendingSince(now.AddHours(-24), batchSize: 10);

        pending.Select(r => r.OrderId).Should().Equal("partial", "accepted");
    }

    [Fact]
    public void 追跡対象は件数上限で打ち切られる()
    {
        var dbName = Guid.NewGuid().ToString();
        var now = new DateTimeOffset(2026, 7, 29, 6, 0, 0, TimeSpan.Zero);
        using (var db = NewContext(dbName))
        {
            var store = new EfExecutedOrderStore(db);
            for (var i = 0; i < 5; i++)
                store.Save(Record($"o{i}", 0m, now.AddMinutes(-10 + i)) with { Status = OrderStatus.Accepted });
        }

        using var db2 = NewContext(dbName);
        new EfExecutedOrderStore(db2).FindPendingSince(now.AddHours(-24), batchSize: 2)
            .Select(r => r.OrderId).Should().Equal("o0", "o1");
    }

    // #270, IADR-0113: 観測した状態は既存行へ反映され、別コンテキストからも読める（新規行は作らない）。
    [Fact]
    public void 観測した約定は既存行を更新し新規行を作らない()
    {
        var dbName = Guid.NewGuid().ToString();
        var now = new DateTimeOffset(2026, 7, 29, 6, 0, 0, TimeSpan.Zero);
        var dispatched = Record("o1", 0m, now.AddMinutes(-3)) with
        {
            Status = OrderStatus.Accepted,
            FilledQuantity = 0,
            AveragePrice = 0m,
        };
        using (var db = NewContext(dbName))
        {
            new EfExecutedOrderStore(db).Save(dispatched);
        }

        using (var db = NewContext(dbName))
        {
            new EfExecutedOrderStore(db)
                .UpdateOutcome("o1", OrderStatus.Filled, 10, 1_005m, 0.005m, now)
                .Should().BeTrue();
        }

        using var db2 = NewContext(dbName);
        var all = new EfExecutedOrderStore(db2).GetAll();
        all.Should().ContainSingle();
        all[0].Status.Should().Be(OrderStatus.Filled);
        all[0].FilledQuantity.Should().Be(10);
        all[0].AveragePrice.Should().Be(1_005m);
        all[0].SlippageRatio.Should().Be(0.005m);
        all[0].ExecutedAt.Should().Be(now);
        all[0].DecisionId.Should().Be(dispatched.DecisionId, "DecisionId 1:1 の相関は変えない");
    }

    [Fact]
    public void 存在しない注文の更新は何もせずfalseを返す()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = NewContext(dbName);
        var store = new EfExecutedOrderStore(db);

        store.UpdateOutcome("missing", OrderStatus.Filled, 10, 100m, 0m, DateTimeOffset.UtcNow)
            .Should().BeFalse();
        store.GetAll().Should().BeEmpty();
    }
}
