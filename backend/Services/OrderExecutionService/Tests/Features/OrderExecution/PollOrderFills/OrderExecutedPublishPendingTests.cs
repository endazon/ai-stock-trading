using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.PollOrderFills;
using OrderExecutionService.Infrastructure.Migrations;
using OrderExecutionService.Infrastructure.Persistence;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 NFR, FR-05, FR-10, #1442（親 #1423 M-4）, IADR-0573 決定2: 約定の記録の更新と OrderExecuted の発行が分かれていても、
// 発行し終えていない約定は次の巡回で再発行される（発行待ちの印）。発行の後に印を倒せば再発行しない。
public class OrderExecutedPublishPendingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 14, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaxTracking = TimeSpan.FromHours(24);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
    }

    private sealed class FakeBroker : IBrokerAdapter
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public Dictionary<string, BrokerOrder?> Orders { get; } = new(StringComparer.Ordinal);

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult(Orders.TryGetValue(orderId, out var o) ? o : null);

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new InvalidOperationException("追跡は発注しない。");

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) =>
            throw new InvalidOperationException("追跡は取消しない。");
    }

    private static OrderIntent Intent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 100, 340m);

    private static ExecutionRecord Dispatched(string orderId, Guid? decisionId = null) =>
        new(decisionId ?? Guid.NewGuid(), orderId, "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, 100, 340m, FilledQuantity: 0, AveragePrice: 0m, OrderStatus.Accepted,
            SlippageRatio: 0m, ExecutedAt: Now.AddMinutes(-5));

    private static BrokerOrder Snapshot(string orderId, OrderStatus status, int filled, decimal price = 341m) =>
        new(orderId, Intent(), status, filled, price, PlacedAt: default,
            CompletedAt: OrderStatusLifecycle.IsTerminal(status) ? Now.AddMinutes(-1) : null);

    // EF（InMemory プロバイダ。毎回新しい DbContext＝別のプロセスから同じ DB を開くのと同じ読み方）とインメモリの両方。
    private static Func<IExecutedOrderStore> Stores(string kind)
    {
        var options = new DbContextOptionsBuilder<OrderExecutionDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var memory = new InMemoryExecutedOrderStore();
        return kind == "ef" ? () => new EfExecutedOrderStore(new OrderExecutionDbContext(options)) : () => memory;
    }

    private static void MarkPublished(IExecutedOrderStore store, OrderFillPollResult result)
    {
        foreach (var e in result.Executed)
            store.MarkOrderExecutedPublished(e.OrderId, e.Status, e.FilledQuantity);
    }

    [Theory]
    [InlineData("ef")]
    [InlineData("inmemory")]
    public void 記録の更新で発行待ちの印が立ち発行した状態と同じときだけ倒れる(string kind)
    {
        var open = Stores(kind);
        open().Save(Dispatched("o1"));
        open().FindOrderExecutedPublishPending(10).Should().BeEmpty("保存（発注の時点）では印を立てない——発注の発行は別の経路");

        open().UpdateOutcome("o1", OrderStatus.PartiallyFilled, 40, 341m, 0m, Now.AddMinutes(-5)).Should().BeTrue();
        open().FindOrderExecutedPublishPending(10).Select(r => r.OrderId).Should().Equal("o1");

        open().MarkOrderExecutedPublished("o1", OrderStatus.Filled, 100).Should().BeFalse("記録の今の状態と違う発行では倒さない");
        open().MarkOrderExecutedPublished("o1", OrderStatus.PartiallyFilled, 30).Should().BeFalse("約定数が違えば倒さない");
        open().FindOrderExecutedPublishPending(10).Should().ContainSingle();

        open().MarkOrderExecutedPublished("o1", OrderStatus.PartiallyFilled, 40).Should().BeTrue();
        open().FindOrderExecutedPublishPending(10).Should().BeEmpty();
        open().MarkOrderExecutedPublished("o1", OrderStatus.PartiallyFilled, 40).Should().BeFalse("印が無ければ何もしない");
        open().MarkOrderExecutedPublished("missing", OrderStatus.Filled, 1).Should().BeFalse();

        // 進めば再び立つ（終端化も入る）。
        open().UpdateOutcome("o1", OrderStatus.Filled, 100, 341m, 0m, Now).Should().BeTrue();
        open().FindOrderExecutedPublishPending(10).Should().ContainSingle()
            .Which.Should().Match<ExecutionRecord>(r => r.Status == OrderStatus.Filled && r.FilledQuantity == 100);
    }

    [Theory]
    [InlineData("ef")]
    [InlineData("inmemory")]
    public void 発行待ちは古い順に上限まで返る(string kind)
    {
        var open = Stores(kind);
        foreach (var (id, minutes) in new[] { ("new", 1), ("old", 30), ("mid", 10) })
        {
            open().Save(Dispatched(id));
            open().UpdateOutcome(id, OrderStatus.Filled, 100, 341m, 0m, Now.AddMinutes(-minutes)).Should().BeTrue();
        }

        open().FindOrderExecutedPublishPending(2).Select(r => r.OrderId).Should().Equal("old", "mid");
    }

    [Fact]
    public void 移行は既存行を発行済みとして扱う列の追加だけである()
    {
        // 既存の約定を全部撃ち直さない（既定 false）。
        var migration = new AddExecutedOrderPublishPending();
        var up = migration.UpOperations.Should().ContainSingle().Which.Should().BeOfType<AddColumnOperation>().Subject;
        up.Table.Should().Be("executed_orders");
        up.Name.Should().Be(nameof(ExecutedOrderRow.OrderExecutedPublishPending));
        up.ClrType.Should().Be<bool>();
        up.IsNullable.Should().BeFalse();
        up.DefaultValue.Should().Be(false);
    }

    [Theory]
    [InlineData("ef")]
    [InlineData("inmemory")]
    public async Task 発行し終えていない約定は次の巡回で記録の値のまま1回だけ再発行される(string kind)
    {
        var open = Stores(kind);
        var decisionId = Guid.NewGuid();
        open().Save(Dispatched("o1", decisionId));
        var broker = new FakeBroker();
        broker.Orders["o1"] = Snapshot("o1", OrderStatus.Filled, 100);
        var clock = new FakeClock();

        // 1 巡回目: 記録を終端化して発行すべき約定を返す。Worker の発行が失敗した（印を倒せなかった）とする。
        var first = await new OrderFillPoller(broker, open(), clock).PollOnceAsync(MaxTracking, 100);
        first.Executed.Should().ContainSingle();
        first.Republished.Should().Be(0);

        // 2 巡回目（別のプロセスで開いた店と同じ読み方）: 記録は終端で追跡の対象ではないが、印が残っているので再発行する。
        clock.UtcNow = Now.AddSeconds(30);
        var second = await new OrderFillPoller(broker, open(), clock).PollOnceAsync(MaxTracking, 100);
        second.Republished.Should().Be(1);
        second.Scanned.Should().Be(0, "終端した記録はブローカーへ照会し直さない（記録が正）");
        second.Executed.Should().ContainSingle().Which.Should().Match<OrderExecuted>(e =>
            e.DecisionId == decisionId && e.OrderId == "o1" && e.Status == OrderStatus.Filled && e.FilledQuantity == 100
            && e.AveragePrice == 341m && e.ExecutedAt == Now.AddMinutes(-1) && e.Provider == BrokerProvider.MoomooSimulate
            && !e.CancelledBySoftwareStop);

        // Worker が発行の後に印を倒せば、以後は再発行しない。
        MarkPublished(open(), second);
        clock.UtcNow = Now.AddSeconds(60);
        var third = await new OrderFillPoller(broker, open(), clock).PollOnceAsync(MaxTracking, 100);
        third.Executed.Should().BeEmpty();
        third.Republished.Should().Be(0);
    }

    [Fact]
    public async Task 発行した後に印を倒せば次の巡回で再発行しない()
    {
        var store = new InMemoryExecutedOrderStore();
        store.Save(Dispatched("o1"));
        var broker = new FakeBroker();
        broker.Orders["o1"] = Snapshot("o1", OrderStatus.PartiallyFilled, 40);
        var poller = new OrderFillPoller(broker, store, new FakeClock());

        var first = await poller.PollOnceAsync(MaxTracking, 100);
        MarkPublished(store, first);
        var second = await poller.PollOnceAsync(MaxTracking, 100);

        first.Executed.Should().ContainSingle();
        second.Executed.Should().BeEmpty("ブローカーの状態は変わらず、発行済みの印も倒れている");
        second.Unchanged.Should().Be(1);
    }

    [Fact]
    public async Task 同じ注文がこの巡回でも進んだら新しい値の1件だけを出す()
    {
        var store = new InMemoryExecutedOrderStore();
        store.Save(Dispatched("o1"));
        var broker = new FakeBroker();
        broker.Orders["o1"] = Snapshot("o1", OrderStatus.PartiallyFilled, 40);
        var poller = new OrderFillPoller(broker, store, new FakeClock());
        await poller.PollOnceAsync(MaxTracking, 100); // 発行に失敗した（印が残る）

        broker.Orders["o1"] = Snapshot("o1", OrderStatus.Filled, 100);
        var second = await poller.PollOnceAsync(MaxTracking, 100);

        second.Republished.Should().Be(0);
        second.Executed.Should().ContainSingle().Which.Should().Match<OrderExecuted>(e =>
            e.Status == OrderStatus.Filled && e.FilledQuantity == 100);
    }

    [Fact]
    public async Task 発行の後に記録が進んでいれば印は残り進んだ状態を再発行する()
    {
        var store = new InMemoryExecutedOrderStore();
        store.Save(Dispatched("o1"));
        var broker = new FakeBroker();
        broker.Orders["o1"] = Snapshot("o1", OrderStatus.PartiallyFilled, 40);
        var poller = new OrderFillPoller(broker, store, new FakeClock());
        var first = await poller.PollOnceAsync(MaxTracking, 100);

        // 発行と印の間に記録が終端まで進んだ（照会し直しの経路など）。古い発行の印倒しは効かない。
        store.UpdateOutcome("o1", OrderStatus.Filled, 100, 341m, 0m, Now).Should().BeTrue();
        MarkPublished(store, first);

        var second = await poller.PollOnceAsync(MaxTracking, 100);
        second.Republished.Should().Be(1);
        second.Executed.Should().ContainSingle().Which.Should().Match<OrderExecuted>(e =>
            e.Status == OrderStatus.Filled && e.FilledQuantity == 100);
    }

    [Fact]
    public async Task S1が取り消した決済の終端を再発行するときも取消の主体を載せる()
    {
        var store = new InMemoryExecutedOrderStore();
        var close = Dispatched("c1") with { PositionEffect = PositionEffect.Close, Side = TradeSide.Sell };
        store.Save(close);
        store.MarkSoftwareStopCancelRequested("c1", Now.AddMinutes(-2)).Should().BeTrue();
        var broker = new FakeBroker();
        broker.Orders["c1"] = Snapshot("c1", OrderStatus.Cancelled, 0, 0m);
        var poller = new OrderFillPoller(broker, store, new FakeClock());

        var first = await poller.PollOnceAsync(MaxTracking, 100);
        var second = await poller.PollOnceAsync(MaxTracking, 100);

        first.Executed.Should().ContainSingle().Which.CancelledBySoftwareStop.Should().BeTrue();
        second.Executed.Should().ContainSingle().Which.CancelledBySoftwareStop.Should().BeTrue(
            "#1395, IADR-0560: 再発行で印を落とすと「手仕舞いが約定せず終了」が出て二重の売りを促す");
    }

    private sealed class FailingPendingLookupStore(InMemoryExecutedOrderStore inner) : IExecutedOrderStore
    {
        public void Save(ExecutionRecord record) => inner.Save(record);
        public IReadOnlyList<ExecutionRecord> GetAll() => inner.GetAll();
        public ExecutionRecord? FindByDecisionId(Guid decisionId) => inner.FindByDecisionId(decisionId);
        public IReadOnlyList<ExecutionRecord> FindPendingSince(DateTimeOffset since, int batchSize) => inner.FindPendingSince(since, batchSize);
        public IReadOnlyList<ExecutionRecord> FindPendingByOrderIds(IReadOnlyCollection<string> orderIds) => inner.FindPendingByOrderIds(orderIds);
        public bool RenewTracking(string orderId, DateTimeOffset trackedFrom) => inner.RenewTracking(orderId, trackedFrom);
        public bool UpdateOutcome(string orderId, OrderStatus status, int filledQuantity, decimal averagePrice, decimal slippageRatio, DateTimeOffset executedAt) =>
            inner.UpdateOutcome(orderId, status, filledQuantity, averagePrice, slippageRatio, executedAt);
        public IReadOnlyList<ExecutionRecord> FindOrderExecutedPublishPending(int batchSize) =>
            throw new InvalidOperationException("DB 断");
    }

    [Fact]
    public async Task 発行待ちの読み取りが失敗しても通常の追跡は止まらない()
    {
        var inner = new InMemoryExecutedOrderStore();
        inner.Save(Dispatched("o1"));
        var broker = new FakeBroker();
        broker.Orders["o1"] = Snapshot("o1", OrderStatus.Filled, 100);

        var result = await new OrderFillPoller(broker, new FailingPendingLookupStore(inner), new FakeClock())
            .PollOnceAsync(MaxTracking, 100);

        result.Executed.Should().ContainSingle().Which.Status.Should().Be(OrderStatus.Filled);
        result.Republished.Should().Be(0);
    }
}
