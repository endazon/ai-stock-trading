using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// FR-10, FR-12, ADR-0040 決定1（S1）, #820, IADR-0344 決定3: S1（ソフトウェア逆指値）の承認を発注執行が受けたときの分岐。
// 受け入れ基準 1: SIMULATE＋S1 の新規買いは保護レグを出さず建玉を保持し、ソフトウェア逆指値が永続化される。
public class OrderExecutionServiceSoftwareStopTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 14, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // #820 の 8 巡目監査, IADR-0344 追記(8) 決定4: S1 の武装は「同一銘柄・同方向に帰属不明の建玉が無いこと」を
    // 前提条件にするため、SIMULATE の発注先と同じく建玉照会を実装する（既定は建玉なし＝帰属不明なし）。
    private sealed class ScriptedBroker(BrokerProvider provider = BrokerProvider.MoomooSimulate)
        : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider { get; } = provider;

        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [];

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Positions);

        public OrderStatus EntryStatus { get; init; } = OrderStatus.Accepted;
        public bool Unavailable { get; init; }

        public int PlaceCount { get; private set; }
        public int StopPlaceCount { get; private set; }
        public int MarketCloseCount { get; private set; }
        public int CancelCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            PlaceCount++;
            if (Unavailable)
                throw new BrokerUnavailableException("OpenD へ接続できません（テスト）");
            var filled = EntryStatus == OrderStatus.Filled ? intent.Quantity : 0;
            return Task.FromResult(new BrokerOrder(
                "entry-1", intent, EntryStatus, filled, filled > 0 ? intent.Price : 0m, Now,
                OrderStatusLifecycle.IsTerminal(EntryStatus) ? Now : null));
        }

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default)
        {
            StopPlaceCount++;
            return Task.FromResult(new BrokerOrder("stop-1", closeIntent, OrderStatus.Rejected, 0, 0m, Now, Now));
        }

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCloseCount++;
            return Task.FromResult(new BrokerOrder("close-1", closeIntent, OrderStatus.Accepted, 0, 0m, Now, null));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            CancelCount++;
            return Task.CompletedTask;
        }
    }

    private static OrderIntent LongEntry() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 1_000m,
            PositionEffect.Open, StopLossPrice: 950m, FxRateToBase: 1m);

    private static OrderApproved S1(OrderIntent? intent = null) =>
        new(Guid.NewGuid(), intent ?? LongEntry(), 10, Now, StopLossMethod: StopLossExecutionMethod.SoftwareStop);

    private static (AppSvc Service, InMemoryExecutedOrderStore Store, InMemoryProtectiveStopOrderStore Stops,
        InMemoryOrderReservationStore Reservations) NewService(IBrokerAdapter broker, bool withStopStore = true)
    {
        var store = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        return (new AppSvc(broker, store, reservations, new FakeClock(), withStopStore ? stops : null),
            store, stops, reservations);
    }

    [Theory]
    [InlineData(OrderStatus.Accepted)]
    [InlineData(OrderStatus.PartiallyFilled)]
    [InlineData(OrderStatus.Filled)]
    public async Task SIMULATEでS1の新規買いは保護レグを出さずソフトウェア逆指値を永続化する(OrderStatus entryStatus)
    {
        var broker = new ScriptedBroker { EntryStatus = entryStatus };
        var (service, _, stops, _) = NewService(broker);
        var approved = S1();

        var result = await service.ExecuteAsync(approved);

        broker.PlaceCount.Should().Be(1, "エントリーは発注する");
        broker.StopPlaceCount.Should().Be(0, "S1 はブローカーへ保護逆指値を出さない");
        broker.CancelCount.Should().Be(0, "S1 はエントリーを取り消さない");
        broker.MarketCloseCount.Should().Be(0, "S1 は発注時に手仕舞わない");
        result.StopPlaced.Should().BeNull();
        result.CoverageLost.Should().BeNull();
        result.StopWaived.Should().BeNull();

        var stop = stops.Find(approved.DecisionId)!;
        stop.Mechanism.Should().Be(StopLossExecutionMethod.SoftwareStop);
        stop.State.Should().Be(ProtectiveStopState.Active);
        stop.TriggerPrice.Should().Be(950m, "損切りラインは承認 Intent の StopLossPrice");
        stop.Quantity.Should().Be(10);
        stop.StopOrderId.Should().BeEmpty("ブローカーに注文は無い");
        stop.StopDecisionId.Should().Be(ProtectiveStopIds.SoftwareStopId(approved.DecisionId));
        stop.Attempt.Should().Be(0);
        stop.TriggeredAt.Should().BeNull();

        var armed = result.SoftwareStopArmed!;
        armed.EntryDecisionId.Should().Be(approved.DecisionId);
        armed.StopLossPrice.Should().Be(950m);
        armed.Provider.Should().Be(BrokerProvider.MoomooSimulate);
    }

    [Theory]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    public async Task S1でエントリーが終端失敗ならソフトウェア逆指値は完了し配置は出ない(OrderStatus entryStatus)
    {
        var broker = new ScriptedBroker { EntryStatus = entryStatus };
        var (service, _, stops, _) = NewService(broker);
        var approved = S1();

        var result = await service.ExecuteAsync(approved);

        result.SoftwareStopArmed.Should().BeNull("建玉が生じない注文に保護は要らない");
        stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        stops.FindActive(10).Should().BeEmpty();
    }

    [Fact]
    public async Task S1で接続断の見送りならソフトウェア逆指値は完了する()
    {
        var broker = new ScriptedBroker { Unavailable = true };
        var (service, _, stops, _) = NewService(broker);
        var approved = S1();

        var result = await service.ExecuteAsync(approved);

        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.BrokerUnavailable);
        stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // 🔴 再配送で配置を重ねない・到達の記録や試行数を巻き戻さない。
    [Fact]
    public async Task S1の再配送は発注も配置も重ねずソフトウェア逆指値を巻き戻さない()
    {
        var broker = new ScriptedBroker { EntryStatus = OrderStatus.Filled };
        var (service, _, stops, _) = NewService(broker);
        var approved = S1();

        await service.ExecuteAsync(approved);
        var triggered = stops.Find(approved.DecisionId)! with { TriggeredAt = Now, TriggeredPrice = 940m, Attempt = 1 };
        stops.Save(triggered);

        var again = await service.ExecuteAsync(approved);

        broker.PlaceCount.Should().Be(1);
        again.SoftwareStopArmed.Should().BeNull();
        stops.Find(approved.DecisionId).Should().Be(triggered);
    }

    [Fact]
    public async Task 保護記録ストアが無ければS1は発注せず見送る()
    {
        var broker = new ScriptedBroker();
        var (service, _, _, _) = NewService(broker, withStopStore: false);

        var result = await service.ExecuteAsync(S1());

        broker.PlaceCount.Should().Be(0, "ソフトウェア逆指値を残せないなら建玉を作らない");
        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.StopOrderUnsupported);
    }

    [Theory]
    [InlineData(BrokerProvider.MoomooReal)]
    [InlineData(BrokerProvider.InternalPaper)]
    public async Task SIMULATE以外ではS1は発注せず記録も作らない(BrokerProvider provider)
    {
        var broker = new ScriptedBroker(provider);
        var (service, _, stops, _) = NewService(broker);

        var result = await service.ExecuteAsync(S1());

        broker.PlaceCount.Should().Be(0);
        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.StopLossMethodNotPermitted);
        stops.FindActive(10).Should().BeEmpty();
    }

    // T-10-434（受け入れ基準 45 の fail-closed 側 / #820 の 8 巡目監査）: 建玉を照会できない巡回では
    // 「帰属不明の建玉が無い」ことを確かめられない。**「不明」を「無い」と取り違えず**、武装せず見送る
    //（他人の建玉を S1 の損切りラインで売るのは無音かつ不可逆な事故である）。
    [Fact]
    public async Task 建玉を照会できないならS1を武装せず見送る()
    {
        var broker = new ScriptedBroker { Positions = null };
        var (service, _, stops, _) = NewService(broker);
        var approved = S1();

        var result = await service.ExecuteAsync(approved);

        ((int)result.Forgone!.Reason).Should().Be(
            4, "OrderDispatchForgoneReason.UnattributedPosition（不明は「ある」側へ倒す）");
        broker.PlaceCount.Should().Be(0, "帰属不明の建玉が無いと確かめられないなら建玉を作らない");
        stops.Find(approved.DecisionId).Should().BeNull();
    }

    // T-10-434（同上）: 建玉照会の能力そのものが無い発注先でも同じ（構成事故を fail-closed で受ける）。
    [Fact]
    public async Task 建玉照会の能力が無い発注先ではS1を武装せず見送る()
    {
        var broker = new PositionBlindBroker();
        var (service, _, stops, _) = NewService(broker);
        var approved = S1();

        var result = await service.ExecuteAsync(approved);

        ((int)result.Forgone!.Reason).Should().Be(4, "OrderDispatchForgoneReason.UnattributedPosition");
        broker.PlaceCount.Should().Be(0);
        stops.Find(approved.DecisionId).Should().BeNull();
    }

    // 建玉照会の能力を持たない発注先（S1 は本来 moomoo SIMULATE でしか選べないが、構成事故を fail-closed で受ける）。
    private sealed class PositionBlindBroker : IBrokerAdapter, IProtectiveOrderBroker
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public int PlaceCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            PlaceCount++;
            return Task.FromResult(new BrokerOrder("entry-blind", intent, OrderStatus.Accepted, 0, 0m, Now, null));
        }

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            Task.FromResult(new BrokerOrder("stop-blind", closeIntent, OrderStatus.Rejected, 0, 0m, Now, Now));

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) =>
            Task.FromResult(new BrokerOrder("close-blind", closeIntent, OrderStatus.Accepted, 0, 0m, Now, null));

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;
    }

    // T-10-435（受け入れ基準 45 / #820 の 8 巡目監査）: 同一銘柄・同方向に**帰属不明の建玉**（主張する保護記録が無い建玉）
    // があるあいだは武装しない。稼働中の S2 から S1 へ切り替えた直後そのものの配置である。
    [Fact]
    public async Task 帰属不明の建玉がある銘柄ではS1を武装せず発注もしない()
    {
        var broker = new ScriptedBroker
        {
            Positions = [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 10, 1_000m)],
        };
        var (service, _, stops, _) = NewService(broker);
        var approved = S1();

        var result = await service.ExecuteAsync(approved);

        ((int)result.Forgone!.Reason).Should().Be(4, "OrderDispatchForgoneReason.UnattributedPosition");
        broker.PlaceCount.Should().Be(0, "建玉を持たずに見送る（IADR-0210 決定1 と同じ倒し方）");
        stops.Find(approved.DecisionId).Should().BeNull("幽霊行の元になる保護記録を作らない");
    }

    [Fact]
    public async Task 空売りはS1でもS0と同じく逆指値を発注しソフトウェア逆指値を作らない()
    {
        var broker = new ScriptedBroker { EntryStatus = OrderStatus.Accepted };
        var (service, _, stops, _) = NewService(broker);
        var shortEntry = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell,
            BrokerProvider.MoomooSimulate, 10, 1_000m, PositionEffect.Open, StopLossPrice: 1_050m, FxRateToBase: 1m);

        var result = await service.ExecuteAsync(S1(shortEntry));

        broker.StopPlaceCount.Should().Be(1, "空売りは ADR-0016 決定2(b) により常に S0");
        result.SoftwareStopArmed.Should().BeNull();
        stops.FindActiveSoftwareStops("AAPL", Market.UnitedStates, TradeSide.Sell).Should().BeEmpty();
    }
}
