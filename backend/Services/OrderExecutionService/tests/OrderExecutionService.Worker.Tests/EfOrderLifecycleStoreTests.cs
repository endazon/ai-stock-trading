using AiStockTrading.OrderExecution.Domain;
using AiStockTrading.OrderExecution.Worker.Foundation.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiStockTrading.OrderExecution.Worker.Tests;

// #154, FR-05, FR-19, IADR-0067: 訂正・取消台帳の永続化を InMemory DB で検証する。
public class EfOrderLifecycleStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 6, 0, 0, TimeSpan.Zero);

    private static OrderExecutionDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<OrderExecutionDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    [Fact]
    public void 訂正はラウンドトリップし別コンテキストからも読める()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();
        var modified = new OrderLifecycleEvent(
            Guid.NewGuid(), decisionId, "o1", OrderLifecycleKind.Modified,
            PreviousQuantity: 10, PreviousPrice: 3000m, Quantity: 4, Price: 2950m, "数量縮小", Now);

        using (var db = NewContext(dbName))
        {
            new EfOrderLifecycleStore(db).Append(modified);
        }

        using var db2 = NewContext(dbName);
        var reloaded = new EfOrderLifecycleStore(db2).GetByOrderId("o1").Should().ContainSingle().Subject;
        reloaded.Kind.Should().Be(OrderLifecycleKind.Modified);
        reloaded.DecisionId.Should().Be(decisionId);
        reloaded.PreviousQuantity.Should().Be(10);
        reloaded.PreviousPrice.Should().Be(3000m);
        reloaded.Quantity.Should().Be(4);
        reloaded.Price.Should().Be(2950m);
        reloaded.Reason.Should().Be("数量縮小");
        reloaded.OccurredAt.Should().Be(Now);
    }

    [Fact]
    public void 取消は数量価格を持たずに記録される()
    {
        var dbName = Guid.NewGuid().ToString();
        var cancelled = new OrderLifecycleEvent(
            Guid.NewGuid(), Guid.NewGuid(), "o1", OrderLifecycleKind.Cancelled,
            PreviousQuantity: null, PreviousPrice: null, Quantity: null, Price: null, "pause による強制取消", Now);

        using var db = NewContext(dbName);
        var store = new EfOrderLifecycleStore(db);
        store.Append(cancelled);

        var reloaded = store.GetByOrderId("o1").Should().ContainSingle().Subject;
        reloaded.Kind.Should().Be(OrderLifecycleKind.Cancelled);
        reloaded.PreviousQuantity.Should().BeNull();
        reloaded.Quantity.Should().BeNull();
        reloaded.Price.Should().BeNull();
    }

    [Fact]
    public void 同一注文の訂正取消は発生順に返る()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();
        using var db = NewContext(dbName);
        var store = new EfOrderLifecycleStore(db);

        // 追記順とは逆順で投入し、並びが OccurredAt で決まることを確かめる。
        store.Append(new OrderLifecycleEvent(
            Guid.NewGuid(), decisionId, "o1", OrderLifecycleKind.Cancelled,
            null, null, null, null, "取消", Now.AddMinutes(2)));
        store.Append(new OrderLifecycleEvent(
            Guid.NewGuid(), decisionId, "o1", OrderLifecycleKind.Modified,
            10, 3000m, 4, 2950m, "訂正", Now.AddMinutes(1)));

        store.GetByOrderId("o1").Select(e => e.Kind).Should()
            .Equal(OrderLifecycleKind.Modified, OrderLifecycleKind.Cancelled);
    }

    [Fact]
    public void 他注文の訂正取消は返さない()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = NewContext(dbName);
        var store = new EfOrderLifecycleStore(db);
        store.Append(new OrderLifecycleEvent(
            Guid.NewGuid(), Guid.NewGuid(), "other", OrderLifecycleKind.Cancelled,
            null, null, null, null, "取消", Now));

        store.GetByOrderId("o1").Should().BeEmpty();
    }
}
