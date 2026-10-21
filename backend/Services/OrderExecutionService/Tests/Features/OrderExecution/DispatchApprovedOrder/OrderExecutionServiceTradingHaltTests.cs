using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// 🔴 T-10-2965〜T-10-2969, FR-10, ADR-0009 決定 3・4, NFR-04, IADR-0565: 新規建てを送る直前の停止状態の最終照会。
// kill switch・一時停止が成立中、または状態が不明なら新規建ては送らない（予約は Forgone・S1 の行と保護の文脈は閉じる）。
// 決済（Close）は照会せずに送る（ADR-0009 決定 4: どの停止でも手仕舞い・損切りは止めない）。
public class OrderExecutionServiceTradingHaltTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 14, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // 照会の回数と、照会の時点でブローカーへ送った回数を記録する（「送る直前」＝照会の時点では未送信であること）。
    private sealed class StubHalt(Func<TradingHaltState?> answer) : ITradingHaltSource
    {
        public int Calls { get; private set; }

        public Func<int>? PlacedAtQuery { get; init; }

        public int? PlacedWhenQueried { get; private set; }

        public Task<TradingHaltState?> GetAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            PlacedWhenQueried = PlacedAtQuery?.Invoke();
            return Task.FromResult(answer());
        }
    }

    private sealed class ThrowingHalt : ITradingHaltSource
    {
        public Task<TradingHaltState?> GetAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("照会元の不具合（テスト）");
    }

    private class Broker : IBrokerAdapter, IProtectiveOrderBroker
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public int PlaceCount { get; private set; }
        public int StopPlaceCount { get; private set; }
        public int MarketCloseCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            PlaceCount++;
            return Task.FromResult(new BrokerOrder(
                "order-" + PlaceCount, intent, OrderStatus.Filled, intent.Quantity, intent.Price, Now, Now));
        }

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default)
        {
            StopPlaceCount++;
            return Task.FromResult(new BrokerOrder("stop-1", closeIntent, OrderStatus.Accepted, 0, 0m, Now, null));
        }

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCloseCount++;
            return Task.FromResult(new BrokerOrder(
                "close-1", closeIntent, OrderStatus.Filled, closeIntent.Quantity, closeIntent.Price, Now, Now));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;
    }

    // S1 の武装は帰属不明の建玉が無いことを建玉照会で確かめる（既定は建玉なし）。
    private sealed class PositionBroker : Broker, IBrokerPositionSource
    {
        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BrokerPositionSnapshot>?>([]);
    }

    private static OrderIntent Entry() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 1_000m,
            PositionEffect.Open, StopLossPrice: 950m, FxRateToBase: 1m);

    private static OrderIntent Close(bool market = false) =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 1_000m,
            PositionEffect.Close, MarketOrder: market);

    private static OrderApproved Approved(OrderIntent intent, StopLossExecutionMethod method = StopLossExecutionMethod.BrokerStopOrder) =>
        new(Guid.NewGuid(), intent, 10, Now, StopLossMethod: method);

    private static (AppSvc Service, Broker Broker, InMemoryOrderReservationStore Reservations,
        InMemoryProtectiveStopOrderStore Stops) NewService(ITradingHaltSource halt, Broker? withBroker = null)
    {
        var broker = withBroker ?? new Broker();
        var reservations = new InMemoryOrderReservationStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var service = new AppSvc(
            broker, new InMemoryExecutedOrderStore(), reservations, new FakeClock(), stops, tradingHalt: halt);
        return (service, broker, reservations, stops);
    }

    public static TheoryData<bool, bool, OrderDispatchForgoneReason> HaltedStates => new()
    {
        // T-10-2965: kill switch だけ成立。
        { true, false, OrderDispatchForgoneReason.KillSwitchActive },
        // T-10-2966: 一時停止だけ成立。
        { false, true, OrderDispatchForgoneReason.TradingPaused },
        // T-10-2969: 両方成立 → kill switch を理由にする（ADR-0009 決定 3 の優先順位）。
        { true, true, OrderDispatchForgoneReason.KillSwitchActive },
    };

    [Theory]
    [MemberData(nameof(HaltedStates))]
    public async Task 停止中は新規建てを送らず予約を見送りとして記録する(
        bool killSwitch, bool paused, OrderDispatchForgoneReason expected)
    {
        Broker? brokerRef = null;
        var halt = new StubHalt(() => new TradingHaltState(killSwitch, paused)) { PlacedAtQuery = () => brokerRef!.PlaceCount };
        var (service, broker, reservations, stops) = NewService(halt);
        brokerRef = broker;
        var approved = Approved(Entry());

        var result = await service.ExecuteAsync(approved);

        halt.Calls.Should().Be(1);
        halt.PlacedWhenQueried.Should().Be(0, "照会はブローカーへ送る前に行う");
        broker.PlaceCount.Should().Be(0, "停止中の新規建てはブローカーへ送らない");
        broker.StopPlaceCount.Should().Be(0);
        result.Executed.Should().BeNull();
        result.Forgone!.Reason.Should().Be(expected);
        result.Forgone.DecisionId.Should().Be(approved.DecisionId);
        reservations.Find(approved.DecisionId)!.State.Should().Be(OrderDispatchState.Forgone, "再配送で送らないよう見送りを記録する");
        stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.Completed, "送らなかったので保護の文脈を閉じる");
    }

    [Fact]
    public async Task 停止中はS1の新規建ても送らずソフトウェア逆指値の行を閉じる()
    {
        var (service, broker, reservations, stops) = NewService(
            new StubHalt(() => new TradingHaltState(false, true)), new PositionBroker());
        var approved = Approved(Entry(), StopLossExecutionMethod.SoftwareStop);

        var result = await service.ExecuteAsync(approved);

        broker.PlaceCount.Should().Be(0);
        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.TradingPaused);
        reservations.Find(approved.DecisionId)!.State.Should().Be(OrderDispatchState.Forgone);
        stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.Completed, "建玉が生じないので S1 の行を残さない");
    }

    // T-10-2967: 照会の失敗（不明）・照会元の例外は送らない。
    [Fact]
    public async Task 状態が不明なら新規建てを送らない()
    {
        var (service, broker, reservations, _) = NewService(new StubHalt(() => null));
        var approved = Approved(Entry());

        var result = await service.ExecuteAsync(approved);

        broker.PlaceCount.Should().Be(0, "不明は送らない側へ倒す");
        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.TradingHaltStateUnknown);
        reservations.Find(approved.DecisionId)!.State.Should().Be(OrderDispatchState.Forgone);
    }

    [Fact]
    public async Task 照会元が例外を投げても新規建てを送らず予約を残さない()
    {
        var (service, broker, reservations, _) = NewService(new ThrowingHalt());
        var approved = Approved(Entry());

        var result = await service.ExecuteAsync(approved);

        broker.PlaceCount.Should().Be(0);
        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.TradingHaltStateUnknown);
        reservations.Find(approved.DecisionId)!.State.Should().Be(OrderDispatchState.Forgone, "Reserved のまま残すと再配送が予約競合で詰まる");
    }

    // T-10-2968: 正常（どちらも成立していない）なら送る。
    [Fact]
    public async Task 停止していなければ新規建てを送る()
    {
        var halt = new StubHalt(() => new TradingHaltState(false, false));
        var (service, broker, reservations, _) = NewService(halt);
        var approved = Approved(Entry());

        var result = await service.ExecuteAsync(approved);

        halt.Calls.Should().Be(1);
        broker.PlaceCount.Should().Be(1);
        broker.StopPlaceCount.Should().Be(1, "保護逆指値も従来どおり張る");
        result.Forgone.Should().BeNull();
        result.Executed!.Status.Should().Be(OrderStatus.Filled);
        reservations.Find(approved.DecisionId)!.State.Should().Be(OrderDispatchState.Completed);
    }

    // T-10-2966: 決済は照会しない＝停止中・照会失敗中でも送る（手仕舞い・損切りは止めない）。
    public static TheoryData<int, bool> CloseCases => new()
    {
        { 0, false }, // kill switch 成立中・指値の決済
        { 1, false }, // 一時停止中
        { 2, false }, // 状態が不明
        { 0, true },  // kill switch 成立中・成行の手仕舞い
        { 2, true },  // 状態が不明・成行の手仕舞い
    };

    [Theory]
    [MemberData(nameof(CloseCases))]
    public async Task 決済は停止状態を照会せずに送る(int haltCase, bool marketClose)
    {
        TradingHaltState? state = haltCase switch
        {
            0 => new TradingHaltState(true, false),
            1 => new TradingHaltState(false, true),
            _ => null,
        };
        var halt = new StubHalt(() => state);
        var (service, broker, _, _) = NewService(halt);

        var result = await service.ExecuteAsync(Approved(Close(marketClose)));

        halt.Calls.Should().Be(0, "決済には最終照会を掛けない（照会の失敗で手仕舞いを止めない）");
        (broker.PlaceCount + broker.MarketCloseCount).Should().Be(1, "決済は送る");
        result.Forgone.Should().BeNull();
        result.Executed.Should().NotBeNull();
    }
}
