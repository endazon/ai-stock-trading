using Microsoft.EntityFrameworkCore;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.PollOrderFills;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 FR-10, FR-11, #1048（利用者裁定 2026-10-02・Q3）, IADR-0481 決定3: 約定追跡の期限（追跡上限）を過ぎて非終端のまま残った注文は、
// 追跡を打ち切ったことを監査へ残す（OrderFillTrackingAbandoned）。免除の記録が発注数量のまま残る状態を追跡できるようにする。
// 作業仕様書 20261002_1048_same-symbol-method-coexistence-and-fill-tracking §受け入れ基準 7〜11。
public class FillTrackingAbandonmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 6, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaxTracking = TimeSpan.FromHours(24);

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class Broker : IBrokerAdapter
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public Dictionary<string, BrokerOrder?> Orders { get; } = [];

        public List<string> Queried { get; } = [];

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default)
        {
            Queried.Add(orderId);
            return Task.FromResult(Orders.TryGetValue(orderId, out var o) ? o : null);
        }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new InvalidOperationException("約定追跡は発注しない");

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) =>
            throw new InvalidOperationException("約定追跡は取り消さない");
    }

    private static readonly OrderIntent EntryIntent =
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 340m);

    private static ExecutionRecord Entry(string orderId, DateTimeOffset at, Guid? decisionId = null) =>
        new(decisionId ?? Guid.NewGuid(), orderId, "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, 10, 340m, FilledQuantity: 0, AveragePrice: 0m, OrderStatus.Accepted, 0m, at);

    private static BrokerOrder Snapshot(string orderId, OrderStatus status, int filled) =>
        new(orderId, EntryIntent, status, filled, filled > 0 ? 340m : 0m, PlacedAt: default,
            CompletedAt: OrderStatusLifecycle.IsTerminal(status) ? Now : null);

    // T-10-2110: 受け入れ基準 7。期限を過ぎた非終端の注文を打ち切る直前に 1 回照会し、非終端（不明・変化なし・一部約定の進捗）なら
    // 打ち切りの事実を返す。内容は相関・注文・最後の状態・約定数・追跡の起点・追跡上限・発注先。期限内の注文は打ち切らない。
    [Theory]
    [InlineData(null, 0, OrderStatus.Accepted, 0)]
    [InlineData(OrderStatus.Accepted, 0, OrderStatus.Accepted, 0)]
    [InlineData(OrderStatus.PartiallyFilled, 4, OrderStatus.PartiallyFilled, 4)]
    public async Task T_10_2110_期限を過ぎて非終端なら打ち切りを返す(
        OrderStatus? brokerStatus, int brokerFilled, OrderStatus expectedStatus, int expectedFilled)
    {
        var store = new InMemoryExecutedOrderStore();
        var broker = new Broker();
        var poller = new OrderFillPoller(broker, store, new MutableClock(Now));
        var decisionId = Guid.NewGuid();
        var trackedFrom = Now.AddHours(-25);
        store.Save(Entry("ORD-OLD", trackedFrom, decisionId));
        store.Save(Entry("ORD-NEW", Now.AddHours(-1)));
        if (brokerStatus is { } status)
            broker.Orders["ORD-OLD"] = Snapshot("ORD-OLD", status, brokerFilled);

        var result = await poller.PollOnceAsync(MaxTracking, batchSize: 100);

        var abandoned = result.Abandoned!.Should().ContainSingle().Subject;
        abandoned.DecisionId.Should().Be(decisionId);
        abandoned.OrderId.Should().Be("ORD-OLD");
        abandoned.Symbol.Should().Be("AAPL");
        abandoned.PositionEffect.Should().Be(PositionEffect.Open);
        abandoned.Quantity.Should().Be(10);
        abandoned.LastStatus.Should().Be(expectedStatus);
        abandoned.FilledQuantity.Should().Be(expectedFilled, "打ち切る直前の照会で進んだ約定は反映して残す");
        abandoned.TrackedFrom.Should().Be(trackedFrom);
        abandoned.MaxTracking.Should().Be(MaxTracking);
        abandoned.Provider.Should().Be(BrokerProvider.MoomooSimulate);
        abandoned.AbandonedAt.Should().Be(Now);
    }

    // T-10-2111: 受け入れ基準 8（窓の後の端・減る側のプローブ）。最後の巡回の後・期限の前に終端していた注文は、打ち切る直前の照会で
    // 終端を見つけ、通常どおり約定を記録して**打ち切らない**（期限だけを見る形では「終端したのに打ち切った」と残る）。
    [Theory]
    [InlineData(OrderStatus.Filled, 10)]
    [InlineData(OrderStatus.Cancelled, 0)]
    public async Task T_10_2111_打ち切る直前の照会で終端していれば打ち切らない(OrderStatus terminal, int filled)
    {
        var store = new InMemoryExecutedOrderStore();
        var broker = new Broker();
        var poller = new OrderFillPoller(broker, store, new MutableClock(Now));
        store.Save(Entry("ORD-OLD", Now.AddHours(-25)));
        broker.Orders["ORD-OLD"] = Snapshot("ORD-OLD", terminal, filled);

        var result = await poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Abandoned.Should().BeEmpty();
        result.Executed.Should().ContainSingle(e => e.OrderId == "ORD-OLD" && e.Status == terminal && e.FilledQuantity == filled);
        store.FindByDecisionId(store.GetAll().Single().DecisionId)!.Status.Should().Be(terminal);
    }

    // T-10-2112: 受け入れ基準 9。印を書いた打ち切りは二度と照会・発行しない（毎巡回の照会を増やさない）。
    // 窓の後の端・増える側のプローブ: 追跡の起点が進められて窓へ戻り（RenewTracking）、再び期限を過ぎたら**別の打ち切り**として改めて返す。
    [Fact]
    public async Task T_10_2112_印の後は打ち切らず起点が進んで再び期限を過ぎたら改めて打ち切る()
    {
        var store = new InMemoryExecutedOrderStore();
        var broker = new Broker();
        var clock = new MutableClock(Now);
        var poller = new OrderFillPoller(broker, store, clock);
        var first = Now.AddHours(-25);
        store.Save(Entry("ORD-OLD", first));

        var r1 = await poller.PollOnceAsync(MaxTracking, batchSize: 100);
        r1.Abandoned!.Single().TrackedFrom.Should().Be(first);
        store.MarkTrackingAbandoned("ORD-OLD", first).Should().BeTrue();

        var r2 = await poller.PollOnceAsync(MaxTracking, batchSize: 100);
        r2.Abandoned.Should().BeEmpty("同じ追跡の起点の打ち切りは 1 回だけ");
        broker.Queried.Should().Equal(["ORD-OLD"], "印を書いた後は照会しない");

        // 常駐ガードが追跡の起点を進め（IADR-0406 決定3）、窓へ戻った。その窓の中では打ち切らない。
        var renewed = Now.AddHours(-1);
        store.RenewTracking("ORD-OLD", renewed).Should().BeTrue();
        (await poller.PollOnceAsync(MaxTracking, batchSize: 100)).Abandoned.Should().BeEmpty();

        // 再び期限を過ぎた。
        clock.UtcNow = Now.AddHours(24);
        var r4 = await poller.PollOnceAsync(MaxTracking, batchSize: 100);
        r4.Abandoned!.Single().TrackedFrom.Should().Be(renewed, "起点が違えば別の打ち切り");
    }

    // T-10-2113: 受け入れ基準 10。Active な S0 の逆指値レグは追跡上限の対象外（IADR-0406 決定2）なので打ち切らない。
    [Fact]
    public async Task T_10_2113_有効な保護記録の逆指値レグは打ち切らない()
    {
        var store = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var broker = new Broker();
        var poller = new OrderFillPoller(broker, store, new MutableClock(Now), protectiveStops: stops);
        var entry = Guid.NewGuid();
        stops.Save(new ProtectiveStopOrder(
            entry, ProtectiveStopIds.StopDecisionId(entry, attempt: 1), "stop-live", "AAPL", Market.UnitedStates,
            TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, 1, ProtectiveStopState.Active,
            Now.AddDays(-3), Now.AddDays(-3)));
        store.Save(new ExecutionRecord(
            ProtectiveStopIds.StopDecisionId(entry, attempt: 1), "stop-live", "AAPL", Market.UnitedStates, TradeSide.Sell,
            ProductType.Cash, PositionEffect.Close, 10, 950m, 0, 0m, OrderStatus.Accepted, 0m, Now.AddDays(-3)));

        var result = await poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Abandoned.Should().BeEmpty("保護が有効なあいだは期限なく追跡する");
        broker.Queried.Should().Equal(["stop-live"], "通常の追跡（上限の対象外）で 1 回だけ照会する");
    }

    // T-10-2114: 受け入れ基準 11。打ち切りの洗い出しと印の意味論を、本番の DB 実装とインメモリ実装で同一に固定する。
    // 既定の実装（試験用の包み型）は何も返さず印も書かない。
    [Theory]
    [InlineData("ef")]
    [InlineData("inmemory")]
    public void T_10_2114_期限を過ぎた未打ち切りの記録の洗い出しと印(string kind)
    {
        IExecutedOrderStore store = kind == "ef"
            ? new EfExecutedOrderStore(new OrderExecutionDbContext(
                new DbContextOptionsBuilder<OrderExecutionDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options))
            : new InMemoryExecutedOrderStore();
        var before = Now.AddHours(-24);
        store.Save(Entry("old-2", Now.AddHours(-26)));
        store.Save(Entry("old-1", Now.AddHours(-30)));
        store.Save(Entry("new", Now.AddHours(-1)));
        store.Save(Entry("old-filled", Now.AddHours(-30)) with { Status = OrderStatus.Filled, FilledQuantity = 10 });

        store.FindTrackingExpired(before, 10).Select(r => r.OrderId).Should().Equal(["old-1", "old-2"], "非終端・期限切れだけを古い順");
        store.FindTrackingExpired(before, 1).Select(r => r.OrderId).Should().Equal(["old-1"]);

        store.MarkTrackingAbandoned("old-1", Now.AddHours(-29)).Should().BeFalse("起点が違う（別の追跡）なら印を書かない");
        store.MarkTrackingAbandoned("missing", Now).Should().BeFalse();
        store.MarkTrackingAbandoned("old-1", Now.AddHours(-30)).Should().BeTrue();
        store.FindTrackingExpired(before, 10).Select(r => r.OrderId).Should().Equal(["old-2"]);

        store.RenewTracking("old-1", Now.AddHours(-25)).Should().BeTrue();
        store.FindTrackingExpired(before, 10).Select(r => r.OrderId).Should().Equal(["old-2", "old-1"], "起点が進めば改めて対象（新しい起点で古い順に並ぶ）");
    }
}
