using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, ADR-0040 決定1（S1）, #820（#826 項目 3）, IADR-0344 決定6: 保護逆指値ガードとソフトウェア逆指値。
// 受け入れ基準 6（到達済みの再試行）・7（ブローカー照会をしない・建玉消滅で解消）・8（手法混在の按分）を固定する。
public class ProtectiveStopGuardSoftwareStopTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 16, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class GuardBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [];
        public Dictionary<string, BrokerOrder> Orders { get; } = new();

        public List<string> OrderQueries { get; } = [];
        public List<string> Cancelled { get; } = [];
        public int MarketCloseCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            throw new NotSupportedException("S1 の行に逆指値を再発注してはならない");

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCloseCount++;
            return Task.FromResult(new BrokerOrder(
                $"close-{MarketCloseCount}", closeIntent, OrderStatus.Accepted, 0, 0m, Now, null));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default)
        {
            OrderQueries.Add(orderId);
            return Task.FromResult(Orders.TryGetValue(orderId, out var order) ? order : null);
        }

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            Cancelled.Add(orderId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Positions);
    }

    private sealed record Fixture(
        ProtectiveStopGuard Guard, GuardBroker Broker, InMemoryProtectiveStopOrderStore Stops, InMemoryExecutedOrderStore Store);

    private static Fixture NewFixture()
    {
        var broker = new GuardBroker();
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var clock = new FakeClock();
        var executor = new SoftwareStopExecutor(broker, broker, stops, store, reservations, clock);
        return new Fixture(new ProtectiveStopGuard(broker, broker, stops, store, clock, executor), broker, stops, store);
    }

    private static BrokerPositionSnapshot Long(int qty) => new("AAPL", Market.UnitedStates, qty, 1_000m);

    private static ProtectiveStopOrder SoftwareStop(DateTimeOffset? triggeredAt = null)
    {
        var id = Guid.NewGuid();
        return new ProtectiveStopOrder(
            id, ProtectiveStopIds.SoftwareStopId(id), string.Empty, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, 0, ProtectiveStopState.Active,
            Now.AddHours(-1), Now.AddHours(-1), StopLossExecutionMethod.SoftwareStop,
            triggeredAt, triggeredAt is null ? null : 940m);
    }

    private static ProtectiveStopOrder BrokerStop(int quantity, string stopOrderId = "stop-s0")
    {
        var id = Guid.NewGuid();
        return new ProtectiveStopOrder(
            id, ProtectiveStopIds.StopDecisionId(id, 1), stopOrderId, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, 900m, 1m, 1, ProtectiveStopState.Active,
            Now.AddHours(-2), Now.AddHours(-2));
    }

    private static void EntryRecord(Fixture f, ProtectiveStopOrder stop, OrderStatus status, int filled) =>
        f.Store.Save(new ExecutionRecord(
            stop.EntryDecisionId, $"entry-{stop.EntryDecisionId:N}", "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, PositionEffect.Open, stop.Quantity, 1_000m, filled, 1_000m, status, 0m, Now.AddHours(-1)));

    [Fact]
    public async Task 未到達のソフトウェア逆指値はブローカーの注文照会をせず建玉があれば維持する()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        EntryRecord(f, stop, OrderStatus.Filled, 10);
        f.Broker.Positions = [Long(10)];

        var result = await f.Guard.RunOnceAsync(10);

        result.StillActive.Should().Be(1);
        f.Broker.OrderQueries.Should().BeEmpty("ブローカーにソフトウェア逆指値の注文は無い");
        f.Broker.Cancelled.Should().BeEmpty();
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    [Fact]
    public async Task 建玉が消えたソフトウェア逆指値は完了し何も発注しない()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        EntryRecord(f, stop, OrderStatus.Filled, 10);
        f.Broker.Positions = []; // 手動決済済み

        var result = await f.Guard.RunOnceAsync(10);

        result.Completed.Should().Be(1);
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        f.Broker.OrderQueries.Should().BeEmpty();
        f.Broker.MarketCloseCount.Should().Be(0);
        f.Broker.Cancelled.Should().BeEmpty();
    }

    [Fact]
    public async Task 約定0で終端したエントリーのソフトウェア逆指値は完了する()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        EntryRecord(f, stop, OrderStatus.Expired, 0); // 当日限りで失効

        await f.Guard.RunOnceAsync(10);

        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    [Theory]
    [InlineData(OrderStatus.Accepted)]
    [InlineData(OrderStatus.PartiallyFilled)]
    public async Task エントリーが未終端なら建玉が0でもソフトウェア逆指値を完了しない(OrderStatus status)
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        EntryRecord(f, stop, status, 0);
        f.Broker.Positions = [];

        var result = await f.Guard.RunOnceAsync(10);

        result.StillActive.Should().Be(1, "これから約定し得るエントリーの保護を外さない");
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    [Fact]
    public async Task 到達済みで決済できていないソフトウェア逆指値はガードが決済を再試行する()
    {
        // 再起動耐性: 到達は記録済み（発注執行の停止・接続断で決済が据え置かれた）。次の到達を待たずに決済する。
        var f = NewFixture();
        var stop = SoftwareStop(triggeredAt: Now.AddMinutes(-3));
        f.Stops.Save(stop);
        EntryRecord(f, stop, OrderStatus.Filled, 10);
        f.Broker.Positions = [Long(10)];

        var result = await f.Guard.RunOnceAsync(10);

        f.Broker.MarketCloseCount.Should().Be(1);
        result.ClosedOut.Should().Be(1);
        result.Events.OfType<SoftwareStopExecuted>().Should().ContainSingle()
            .Which.Outcome.Should().Be(SoftwareStopOutcome.ClosePlaced);
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);

        // 次の巡回では何もしない（二重決済なし）。
        await f.Guard.RunOnceAsync(10);
        f.Broker.MarketCloseCount.Should().Be(1);
    }

    // #826 項目 3 の S1 側: S1 の建玉が同じ銘柄に残っていても、S0 の建玉が消えたら S0 の逆指値を取り消す。
    [Fact]
    public async Task S0の建玉残はソフトウェア逆指値の約定数量を差し引いて判定する()
    {
        var f = NewFixture();
        var s0 = BrokerStop(quantity: 5);
        var s1 = SoftwareStop();
        f.Stops.Save(s0);
        f.Stops.Save(s1);
        EntryRecord(f, s1, OrderStatus.Filled, 10);
        f.Broker.Orders["stop-s0"] = new BrokerOrder(
            "stop-s0", new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 5, 900m, PositionEffect.Close), OrderStatus.Accepted, 0, 0m, Now, null);
        f.Broker.Positions = [Long(10)]; // S0 の 5 は決済済み、残りは S1 の 10 だけ

        await f.Guard.RunOnceAsync(10);

        f.Broker.Cancelled.Should().ContainSingle().Which.Should().Be("stop-s0",
            "S1 の建玉で S0 の逆指値を生かし続けると、決済後に反対建玉を生む");
        f.Stops.Find(s0.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        f.Stops.Find(s1.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active, "S1 の建玉は残っている");
    }

    [Fact]
    public async Task S1が無ければS0の建玉残の判定は従来と同一()
    {
        var f = NewFixture();
        var s0 = BrokerStop(quantity: 5);
        f.Stops.Save(s0);
        f.Broker.Orders["stop-s0"] = new BrokerOrder(
            "stop-s0", new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 5, 900m, PositionEffect.Close), OrderStatus.Accepted, 0, 0m, Now, null);
        f.Broker.Positions = [Long(3)];

        var result = await f.Guard.RunOnceAsync(10);

        result.StillActive.Should().Be(1);
        f.Broker.Cancelled.Should().BeEmpty();
    }
}
