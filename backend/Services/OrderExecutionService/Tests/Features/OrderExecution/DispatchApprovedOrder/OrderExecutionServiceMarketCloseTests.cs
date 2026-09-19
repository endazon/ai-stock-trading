using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// 🔴 T-10-584, FR-05, FR-10, UC-06, #847, IADR-0357: 成行の手仕舞いは既存の `IProtectiveOrderBroker.PlaceMarketOrderAsync`
// （IADR-0210 で入った「逆指値が成立しないときの建玉解消」の口）へ送る。**新しいブローカー呼び出しを作らない。**
//
// 稼働環境（2026-09-18 23:13 JST）の実測: 現在値の売り指値 334.09 が、直後の下落（333.59）で約定せず板に残った。
public class OrderExecutionServiceMarketCloseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 6, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // 指値経路（PlaceOrderAsync）と成行経路（PlaceMarketOrderAsync）のどちらへ送られたかを記録する。
    private sealed class RecordingBroker : IBrokerAdapter, IProtectiveOrderBroker
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public int LimitCount { get; private set; }

        public int MarketCount { get; private set; }

        public OrderIntent? LastIntent { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            LimitCount++;
            LastIntent = intent;
            return Task.FromResult(new BrokerOrder("limit-1", intent, OrderStatus.Accepted, 0, 0m, Now, null));
        }

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCount++;
            LastIntent = closeIntent;
            return Task.FromResult(new BrokerOrder(
                "market-1", closeIntent, OrderStatus.Filled, closeIntent.Quantity, closeIntent.Price, Now, Now));
        }

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            Task.FromResult(new BrokerOrder("stop-1", closeIntent, OrderStatus.Accepted, 0, 0m, Now, null));

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;
    }

    // 指値・成行のいずれでも決済意図を持たないブローカー（IProtectiveOrderBroker を実装しない）。
    private sealed class LimitOnlyBroker : IBrokerAdapter
    {
        public BrokerProvider Provider => BrokerProvider.InternalPaper;

        public int LimitCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            LimitCount++;
            return Task.FromResult(new BrokerOrder("limit-1", intent, OrderStatus.Accepted, 0, 0m, Now, null));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static OrderIntent CloseIntent(bool marketOrder) =>
        new("SOXL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
            3381, 333.59m, PositionEffect.Close, StopLossPrice: null, FxRateToBase: 1m, MarketOrder: marketOrder);

    private static AppSvc Create(IBrokerAdapter broker, InMemoryExecutedOrderStore? store = null) =>
        new(broker, store ?? new InMemoryExecutedOrderStore(), new InMemoryOrderReservationStore(), new FakeClock());

    [Fact]
    public async Task 成行の手仕舞いは成行の口へ送る()
    {
        var broker = new RecordingBroker();

        await Create(broker).ExecuteAsync(new OrderApproved(Guid.NewGuid(), CloseIntent(true), 3381, Now));

        broker.MarketCount.Should().Be(1);
        broker.LimitCount.Should().Be(0, "成行を指値で送ると #847 の事故（板に残る）が再発する");
    }

    [Fact]
    public async Task 指値の手仕舞いは従来どおり指値の口へ送る()
    {
        var broker = new RecordingBroker();

        await Create(broker).ExecuteAsync(new OrderApproved(Guid.NewGuid(), CloseIntent(false), 3381, Now));

        broker.LimitCount.Should().Be(1);
        broker.MarketCount.Should().Be(0);
    }

    // 否定形: エントリー（Open）は本分岐に入らない。既定 false であり、保護逆指値の同時発注も 1 バイトも変わらない。
    [Fact]
    public async Task エントリーは成行の口へ送らない()
    {
        var broker = new RecordingBroker();
        var entry = new OrderIntent(
            "SOXL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
            10, 333.59m, PositionEffect.Open, StopLossPrice: 320m);

        await Create(broker).ExecuteAsync(new OrderApproved(Guid.NewGuid(), entry, 10, Now));

        broker.MarketCount.Should().Be(0);
        broker.LimitCount.Should().Be(1);
    }

    // 成行の能力が無いブローカーでも**手仕舞いは止めない**（FR-10）。指値（参照価格）で送る。
    [Fact]
    public async Task 成行の能力が無いブローカーでは指値で送り手仕舞いを止めない()
    {
        var broker = new LimitOnlyBroker();

        var result = await Create(broker).ExecuteAsync(
            new OrderApproved(Guid.NewGuid(), CloseIntent(true), 3381, Now));

        broker.LimitCount.Should().Be(1);
        result.Forgone.Should().BeNull("手仕舞いを見送ると建玉が残る。FR-10 は手仕舞いを止めないと定める");
    }

    // ブローカーの実建玉を返す供給口（#864 / IADR-0355 の突合が使う）。
    private sealed class StubPositions(int netQuantity) : IBrokerPositionSource
    {
        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BrokerPositionSnapshot>?>(
                [new BrokerPositionSnapshot("SOXL", Market.UnitedStates, netQuantity, 334.09m)]);
    }

    // 🔴 T-10-591, #847, #864, IADR-0357, IADR-0355（**2 つの PR の合成点**）:
    // **実建玉へ縮められた手仕舞いも、成行のまま送られる。**
    //
    // 突合（#864）は `intent with { Quantity = ... }` で数量だけを差し替える。`with` なので `MarketOrder` は
    // 引き継がれる —— が、これは**暗黙の依存**である。将来ここが位置指定のコンストラクタ呼び出しへ書き換われば
    // **`MarketOrder` が既定 false へ落ち、縮められた手仕舞いだけが静かに指値へ戻る**（＝#847 の再発）。
    // 2 つの PR が別々に正しくても合成点は誰のテストにも入らないため、ここで固定する。
    [Fact]
    public async Task 実建玉へ縮められた手仕舞いも成行のまま送られる()
    {
        var broker = new RecordingBroker();
        var service = new AppSvc(
            broker, new InMemoryExecutedOrderStore(), new InMemoryOrderReservationStore(), new FakeClock(),
            protectiveStops: null, logger: null, brokerPositions: new StubPositions(1_000));

        // 台帳は 3,381 株の手仕舞いを承認したが、ブローカーの実建玉は 1,000 株しかない。
        await service.ExecuteAsync(new OrderApproved(Guid.NewGuid(), CloseIntent(true), 3_381, Now));

        broker.MarketCount.Should().Be(1, "縮めても成行のまま送る");
        broker.LimitCount.Should().Be(0);
        broker.LastIntent!.Quantity.Should().Be(1_000, "実建玉の範囲へ縮まる（#864 の突合）");
        broker.LastIntent.MarketOrder.Should().BeTrue("縮めても成行の指定は失われない");
    }

    [Fact]
    public async Task 成行の手仕舞いも決済の記録として残る()
    {
        var store = new InMemoryExecutedOrderStore();
        var decisionId = Guid.NewGuid();

        await Create(new RecordingBroker(), store)
            .ExecuteAsync(new OrderApproved(decisionId, CloseIntent(true), 3381, Now));

        var record = store.FindByDecisionId(decisionId);
        record.Should().NotBeNull();
        record!.PositionEffect.Should().Be(PositionEffect.Close);
        record.OrderId.Should().Be("market-1");
    }
}
