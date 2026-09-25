using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Features.OrderExecution.PollOrderFills;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, FR-05, UC-02, #958, IADR-0406: 武装から追跡上限（既定 24 時間）を超えて約定したブローカー側逆指値（S0）の約定を
// 約定追跡の窓から落とさない。S0 のレグの記録は武装の時刻で作られるため、上限で切ると損切りが台帳へ届かず、
// IADR-0394 の「S0 は約定で数える」が働かない。時刻はすべて注入時計で進める（壁時計の sleep を使わない）。
public class BrokerStopLegFillTrackingTests
{
    private static readonly DateTimeOffset ArmedAt = new(2026, 9, 22, 14, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaxTracking = TimeSpan.FromHours(24);
    private static readonly DateTimeOffset FilledAt = ArmedAt.AddHours(25);

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    // 約定追跡とガードが同じ発注先を照会する（本番も同じアダプタを共有する）。
    private sealed class StopBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public Dictionary<string, BrokerOrder?> Orders { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [];

        public List<string> Queried { get; } = [];

        public List<string> Cancelled { get; } = [];

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default)
        {
            Queried.Add(orderId);
            return Task.FromResult(Orders.TryGetValue(orderId, out var order) ? order : null);
        }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("このテストでは通常発注をしない");

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            throw new NotSupportedException("このテストでは再発注をしない（建玉は 0）");

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) =>
            throw new NotSupportedException("このテストでは手仕舞いをしない");

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            Cancelled.Add(orderId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Positions);
    }

    private static readonly OrderIntent CloseIntent =
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m,
            PositionEffect.Close);

    // 武装した S0 の保護記録（現試行 1・逆指値の注文 ID stopOrderId）。
    private static ProtectiveStopOrder S0(
        Guid entryDecisionId, string stopOrderId = "stop-1",
        ProtectiveStopState state = ProtectiveStopState.Active) =>
        new(entryDecisionId, ProtectiveStopIds.StopDecisionId(entryDecisionId, 1), stopOrderId, "AAPL",
            Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, 1,
            state, ArmedAt, ArmedAt);

    // 武装の時刻で保存される S0 のレグの記録（Accepted・約定 0。OrderExecutionAppService と同じ形）。
    private static ExecutionRecord Leg(ProtectiveStopOrder stop, DateTimeOffset? at = null) =>
        new(stop.StopDecisionId, stop.StopOrderId, "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, 10, 950m, FilledQuantity: 0, AveragePrice: 0m, OrderStatus.Accepted,
            SlippageRatio: 0m, ExecutedAt: at ?? ArmedAt);

    // #1013, IADR-0428（2026-09-26 追記）: エントリーの発注記録（約定済み・終端）。ガードは建玉 0 を建玉消滅と読む前にこれを見る
    // （未約定なら取り消さない）。この試験は「建って消えた」側を固定するので約定済みで置く。
    private static ExecutionRecord FilledEntry(ProtectiveStopOrder stop) =>
        new(stop.EntryDecisionId, $"entry-{stop.EntryDecisionId:N}", "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, PositionEffect.Open, 10, 1_000m, FilledQuantity: 10, AveragePrice: 1_000m, OrderStatus.Filled,
            SlippageRatio: 0m, ExecutedAt: ArmedAt);

    private static BrokerOrder StopState(string orderId, OrderStatus status, int filled = 0, DateTimeOffset? at = null) =>
        new(orderId, CloseIntent, status, filled, filled > 0 ? 949.5m : 0m, PlacedAt: default,
            CompletedAt: OrderStatusLifecycle.IsTerminal(status) ? at ?? FilledAt : null);

    private sealed record Fixture(
        MutableClock Clock,
        StopBroker Broker,
        InMemoryExecutedOrderStore Store,
        InMemoryProtectiveStopOrderStore Stops,
        OrderFillPoller Poller,
        ProtectiveStopGuard Guard);

    private static Fixture Build(bool withProtectiveStops = true)
    {
        var clock = new MutableClock(FilledAt);
        var broker = new StopBroker();
        var store = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var poller = new OrderFillPoller(
            broker, store, clock, reArmer: null, logger: null, protectiveStops: withProtectiveStops ? stops : null);
        var guard = new ProtectiveStopGuard(broker, broker, stops, store, new InMemoryOrderReservationStore(), clock);
        return new Fixture(clock, broker, store, stops, poller, guard);
    }

    // ---- T-10-862: Active な S0 のレグは追跡上限を越えて照会される ----

    [Fact]
    public async Task 武装から25時間後に約定したS0はOrderExecutedとして発行される()
    {
        // T-10-862, FR-10, #958, IADR-0406 決定2
        var f = Build();
        var stop = S0(Guid.NewGuid());
        f.Stops.Save(stop);
        f.Store.Save(Leg(stop));
        f.Broker.Orders["stop-1"] = StopState("stop-1", OrderStatus.Filled, filled: 10);

        var result = await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Scanned.Should().Be(1);
        result.Terminalized.Should().Be(1);
        var executed = result.Executed.Should().ContainSingle().Subject;
        executed.DecisionId.Should().Be(stop.StopDecisionId);
        executed.OrderId.Should().Be("stop-1");
        executed.Status.Should().Be(OrderStatus.Filled);
        executed.FilledQuantity.Should().Be(10);
        // 約定の時刻（IADR-0394 はこの時刻の取引日で数える）。武装の時刻ではない。
        executed.ExecutedAt.Should().Be(FilledAt);
        f.Store.FindByDecisionId(stop.StopDecisionId)!.Status.Should().Be(OrderStatus.Filled);
    }

    [Fact]
    public async Task 上限を越えたS0のレグの部分約定も発行される()
    {
        // T-10-862, FR-10, #958, IADR-0406 決定2: 部分約定（非終端の進捗）も Active のあいだは届く。
        var f = Build();
        var stop = S0(Guid.NewGuid());
        f.Stops.Save(stop);
        f.Store.Save(Leg(stop));
        f.Broker.Orders["stop-1"] = StopState("stop-1", OrderStatus.PartiallyFilled, filled: 4);

        var result = await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Executed.Should().ContainSingle().Which.FilledQuantity.Should().Be(4);
    }

    [Fact]
    public async Task 上限内のS0のレグは重複して照会されない()
    {
        // T-10-862, #958, IADR-0406 決定2: 窓の内側のレグは既存の抽出に入っている。足すのは窓の外側だけ。
        var f = Build();
        var stop = S0(Guid.NewGuid());
        f.Stops.Save(stop);
        f.Store.Save(Leg(stop, at: FilledAt.AddHours(-1)));
        f.Broker.Orders["stop-1"] = StopState("stop-1", OrderStatus.Accepted);

        var result = await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Scanned.Should().Be(1);
        f.Broker.Queried.Should().Equal("stop-1");
    }

    // ---- T-10-863: 対象外は照会しない（照会件数を増やさない） ----

    [Fact]
    public async Task 上限を越えた記録のうちActiveなS0のレグ以外は照会しない()
    {
        // T-10-863, FR-10, #958, IADR-0406 決定2
        var f = Build();

        // (a) エントリーの記録（保護記録のレグではない）
        f.Store.Save(new ExecutionRecord(
            Guid.NewGuid(), "entry-1", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, 10, 1_000m, 0, 0m, OrderStatus.Accepted, 0m, ArmedAt));

        // (b) 完了済みの S0 の保護記録のレグ
        var completed = S0(Guid.NewGuid(), stopOrderId: "stop-done", state: ProtectiveStopState.Completed);
        f.Stops.Save(completed);
        f.Store.Save(Leg(completed));

        // (c) S1（ソフトウェア逆指値）の行。ブローカーに逆指値が無い（StopOrderId が空）
        f.Stops.Save(S0(Guid.NewGuid(), stopOrderId: string.Empty) with
        {
            Mechanism = StopLossExecutionMethod.SoftwareStop,
        });
        f.Store.Save(new ExecutionRecord(
            Guid.NewGuid(), "s1-close-1", "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, 10, 950m, 0, 0m, OrderStatus.Accepted, 0m, ArmedAt));

        var result = await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Scanned.Should().Be(0);
        f.Broker.Queried.Should().BeEmpty();
    }

    [Fact]
    public async Task 保護記録ストアが未構成なら従来どおり上限内だけを照会する()
    {
        // T-10-863, #958, IADR-0406 決定2: 省略時は従来の挙動（既存の構成・テストを変えない）。
        var f = Build(withProtectiveStops: false);
        var stop = S0(Guid.NewGuid());
        f.Stops.Save(stop);
        f.Store.Save(Leg(stop));
        f.Broker.Orders["stop-1"] = StopState("stop-1", OrderStatus.Filled, filled: 10);

        var result = await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Scanned.Should().Be(0);
        result.Executed.Should().BeEmpty();
    }

    // 注文 ID 指定の抽出だけが失敗する発注結果ストア（足す側の読み取りの失敗を再現する）。
    private sealed class FailingStopLegLookupStore(InMemoryExecutedOrderStore inner) : IExecutedOrderStore
    {
        public void Save(ExecutionRecord record) => inner.Save(record);

        public IReadOnlyList<ExecutionRecord> GetAll() => inner.GetAll();

        public ExecutionRecord? FindByDecisionId(Guid decisionId) => inner.FindByDecisionId(decisionId);

        public IReadOnlyList<ExecutionRecord> FindPendingSince(DateTimeOffset since, int batchSize) =>
            inner.FindPendingSince(since, batchSize);

        public IReadOnlyList<ExecutionRecord> FindPendingByOrderIds(IReadOnlyCollection<string> orderIds) =>
            throw new InvalidOperationException("DB 障害（テスト）");

        public bool RenewTracking(string orderId, DateTimeOffset trackedFrom) =>
            inner.RenewTracking(orderId, trackedFrom);

        public bool UpdateOutcome(
            string orderId, OrderStatus status, int filledQuantity, decimal averagePrice,
            decimal slippageRatio, DateTimeOffset executedAt) =>
            inner.UpdateOutcome(orderId, status, filledQuantity, averagePrice, slippageRatio, executedAt);
    }

    [Fact]
    public async Task 上限を越えたレグを足す読み取りが失敗しても上限内の追跡は続く()
    {
        // T-10-863, #958, IADR-0406 決定2: 足す側の失敗で通常の追跡を止めない（次の巡回で再び足す）。
        var inner = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var broker = new StopBroker();
        var poller = new OrderFillPoller(
            broker, new FailingStopLegLookupStore(inner), new MutableClock(FilledAt), protectiveStops: stops);
        var stop = S0(Guid.NewGuid());
        stops.Save(stop);
        inner.Save(Leg(stop));
        var entryDecisionId = Guid.NewGuid();
        inner.Save(new ExecutionRecord(
            entryDecisionId, "entry-1", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, 10, 1_000m, 0, 0m, OrderStatus.Accepted, 0m, FilledAt.AddMinutes(-3)));
        broker.Orders["entry-1"] = new BrokerOrder(
            "entry-1", CloseIntent, OrderStatus.Filled, 10, 1_000m, PlacedAt: default, CompletedAt: FilledAt);

        var result = await poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Executed.Should().ContainSingle().Which.DecisionId.Should().Be(entryDecisionId);
        broker.Queried.Should().Equal("entry-1");
    }

    // ---- T-10-864: ガードは S0 のレグの終端を観測したら、完了させる前に追跡の起点を進める ----

    [Theory]
    [InlineData(OrderStatus.Filled)]
    [InlineData(OrderStatus.Expired)]
    [InlineData(OrderStatus.Cancelled)]
    public async Task ガードはS0のレグの終端を見たらレグの記録を窓へ戻してから完了させる(OrderStatus observed)
    {
        // T-10-864, FR-10, #958, IADR-0406 決定3
        var f = Build();
        var stop = S0(Guid.NewGuid());
        f.Stops.Save(stop);
        f.Store.Save(Leg(stop));
        f.Store.Save(FilledEntry(stop));
        f.Broker.Orders["stop-1"] = StopState("stop-1", observed, filled: observed == OrderStatus.Filled ? 10 : 3);
        f.Broker.Positions = []; // 逆指値の約定・失効で建玉は 0

        var guardResult = await f.Guard.RunOnceAsync(10);

        guardResult.Completed.Should().Be(1);
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        var leg = f.Store.FindByDecisionId(stop.StopDecisionId)!;
        leg.ExecutedAt.Should().Be(FilledAt);                 // 追跡の起点だけが観測の時刻へ進む
        leg.Status.Should().Be(OrderStatus.Accepted);         // 状態・数量は書かない（反映は約定追跡の役目）
        leg.FilledQuantity.Should().Be(0);
    }

    [Fact]
    public async Task ガードが建玉消滅で逆指値を取り消すときもレグの記録を窓へ戻す()
    {
        // T-10-864, FR-10, #958, IADR-0406 決定3: 取消の直前までの部分約定を約定追跡に拾わせる。
        var f = Build();
        var stop = S0(Guid.NewGuid());
        f.Stops.Save(stop);
        f.Store.Save(Leg(stop));
        f.Store.Save(FilledEntry(stop));
        f.Broker.Orders["stop-1"] = StopState("stop-1", OrderStatus.PartiallyFilled, filled: 3);
        f.Broker.Positions = [];

        await f.Guard.RunOnceAsync(10);

        f.Broker.Cancelled.Should().Equal("stop-1");
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        f.Store.FindByDecisionId(stop.StopDecisionId)!.ExecutedAt.Should().Be(FilledAt);
    }

    [Fact]
    public async Task 約定追跡が反映済みのレグの記録はガードが書き換えない()
    {
        // T-10-864, #958, IADR-0406 決定3: 終端の記録（約定追跡が先に反映した）は触らない。
        var f = Build();
        var stop = S0(Guid.NewGuid());
        f.Stops.Save(stop);
        f.Store.Save(Leg(stop) with
        {
            Status = OrderStatus.Filled,
            FilledQuantity = 10,
            AveragePrice = 949.5m,
            ExecutedAt = FilledAt.AddMinutes(-1),
        });
        f.Broker.Orders["stop-1"] = StopState("stop-1", OrderStatus.Filled, filled: 10);

        await f.Guard.RunOnceAsync(10);

        var leg = f.Store.FindByDecisionId(stop.StopDecisionId)!;
        leg.ExecutedAt.Should().Be(FilledAt.AddMinutes(-1));
        leg.Status.Should().Be(OrderStatus.Filled);
    }

    // ---- T-10-866: ガードと約定追跡の巡回の順序に依らず、約定は 1 回だけ届く（規則 11 のプローブ） ----

    [Fact]
    public async Task ガードが先に約定を見ても次の約定追跡で発行される()
    {
        // T-10-866, FR-10, #958, IADR-0406 決定2・決定3: 形 B（Active だけ対象外）はこの順序で落ちる。
        var f = Build();
        var stop = S0(Guid.NewGuid());
        f.Stops.Save(stop);
        f.Store.Save(Leg(stop));
        f.Broker.Orders["stop-1"] = StopState("stop-1", OrderStatus.Filled, filled: 10);
        f.Broker.Positions = [];

        await f.Guard.RunOnceAsync(10);
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);

        f.Clock.UtcNow = FilledAt.AddSeconds(30);
        var first = await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);
        f.Clock.UtcNow = FilledAt.AddSeconds(60);
        var second = await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);

        first.Executed.Should().ContainSingle().Which.Should().Match<OrderExecuted>(e =>
            e.DecisionId == stop.StopDecisionId && e.Status == OrderStatus.Filled && e.FilledQuantity == 10);
        second.Executed.Should().BeEmpty();
    }

    [Fact]
    public async Task 約定追跡が先に約定を見てもガードは記録を巻き戻さず完了させる()
    {
        // T-10-866, FR-10, #958, IADR-0406 決定2・決定3
        var f = Build();
        var stop = S0(Guid.NewGuid());
        f.Stops.Save(stop);
        f.Store.Save(Leg(stop));
        f.Broker.Orders["stop-1"] = StopState("stop-1", OrderStatus.Filled, filled: 10);
        f.Broker.Positions = [];

        var first = await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);
        f.Clock.UtcNow = FilledAt.AddSeconds(30);
        await f.Guard.RunOnceAsync(10);
        f.Clock.UtcNow = FilledAt.AddSeconds(60);
        var second = await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);

        first.Executed.Should().ContainSingle().Which.Status.Should().Be(OrderStatus.Filled);
        second.Executed.Should().BeEmpty();
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        f.Store.FindByDecisionId(stop.StopDecisionId)!.Status.Should().Be(OrderStatus.Filled);
    }
}
