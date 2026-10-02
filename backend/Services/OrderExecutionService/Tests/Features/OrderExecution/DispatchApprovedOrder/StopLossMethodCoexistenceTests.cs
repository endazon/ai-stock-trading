using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// 🔴 FR-10, ADR-0040 決定1, #1048（利用者裁定 2026-10-02・Q2 案 b）, IADR-0481 決定1・決定2:
// 同じ銘柄・同じ向きに別の損切りの実行機構の有効な建玉が残っている間は、新しい手法での新規建てを見送る（理由つき）。決済は止めない。
// 作業仕様書 20261002_1048_same-symbol-method-coexistence-and-fill-tracking §受け入れ基準 1〜6。
public class StopLossMethodCoexistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 14, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class Broker(BrokerProvider provider = BrokerProvider.MoomooSimulate)
        : IBrokerAdapter, IProtectiveOrderBroker, IAlternativeProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider { get; } = provider;

        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [];

        public int PositionQueries { get; private set; }

        public int PlaceCount { get; private set; }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default)
        {
            PositionQueries++;
            return Task.FromResult(Positions);
        }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            PlaceCount++;
            return Task.FromResult(new BrokerOrder($"order-{PlaceCount}", intent, OrderStatus.Accepted, 0, 0m, Now, null));
        }

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            Task.FromResult(new BrokerOrder("stop-1", closeIntent, OrderStatus.Accepted, 0, 0m, Now, null));

        public AlternativeProtectiveOrderType AlternativeProtectiveOrderType => AlternativeProtectiveOrderType.StopLimit;

        // 本試験の S3 は保護レグまで到達しない（見送りの判定だけを見る）。
        public Task<AlternativeProtectiveOrderPlacement> PlaceAlternativeStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, decimal entryReferencePrice, Guid decisionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("本試験では S3 の保護レグを送らない");

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            PlaceCount++;
            return Task.FromResult(new BrokerOrder("close-1", closeIntent, OrderStatus.Accepted, 0, 0m, Now, null));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;
    }

    // 後の端のプローブ（増える側）: 予約を取った瞬間に、別の手法の記録が保護記録ストアへ現れる（並行した別の承認を模す）。
    private sealed class InjectingReservations(Action onReserve) : IOrderReservationStore
    {
        private readonly InMemoryOrderReservationStore _inner = new();

        public bool TryReserve(Guid decisionId, DateTimeOffset reservedAt, BrokerProvider? brokerProvider)
        {
            var reserved = _inner.TryReserve(decisionId, reservedAt, brokerProvider);
            if (reserved)
                onReserve();
            return reserved;
        }

        public void MarkCompleted(Guid decisionId, string brokerOrderId, DateTimeOffset completedAt) =>
            _inner.MarkCompleted(decisionId, brokerOrderId, completedAt);

        public OrderDispatchReservation? Find(Guid decisionId) => _inner.Find(decisionId);

        public IReadOnlyList<OrderDispatchReservation> FindStalledReserved(DateTimeOffset reservedBefore, int batchSize) =>
            _inner.FindStalledReserved(reservedBefore, batchSize);

        public ForgoneRecordOutcome TryRecordForgone(Guid decisionId, DateTimeOffset forgoneAt) =>
            _inner.TryRecordForgone(decisionId, forgoneAt);

        public ForgoneRecordOutcome MarkReservationForgone(Guid decisionId, DateTimeOffset forgoneAt) =>
            _inner.MarkReservationForgone(decisionId, forgoneAt);

        public bool Release(Guid decisionId) => _inner.Release(decisionId);

        public int PurgeCompletedBefore(DateTimeOffset completedBefore, int batchSize) =>
            _inner.PurgeCompletedBefore(completedBefore, batchSize);
    }

    private static OrderIntent Intent(
        string symbol = "AAPL", TradeSide side = TradeSide.Buy, PositionEffect effect = PositionEffect.Open,
        BrokerProvider mode = BrokerProvider.MoomooSimulate) =>
        new(symbol, Market.UnitedStates, side, ProductType.Cash, mode, 10, 1_000m, effect,
            StopLossPrice: effect == PositionEffect.Open ? 950m : null, FxRateToBase: 1m);

    private static OrderApproved Approved(StopLossExecutionMethod method, OrderIntent? intent = null) =>
        new(Guid.NewGuid(), intent ?? Intent(), 10, Now, StopLossMethod: method);

    private static ProtectiveStopOrder Row(
        StopLossExecutionMethod mechanism, ProtectiveStopState state = ProtectiveStopState.Active,
        string symbol = "AAPL", TradeSide side = TradeSide.Buy, int quantity = 5) =>
        new(Guid.NewGuid(), Guid.NewGuid(), mechanism == StopLossExecutionMethod.SoftwareStop ? string.Empty : "stop-x",
            symbol, Market.UnitedStates, side, ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, 950m, 1m, 1,
            state, Now.AddMinutes(-5), Now.AddMinutes(-5), Mechanism: mechanism,
            RemainingProtected: mechanism == StopLossExecutionMethod.SoftwareStop ? quantity : null);

    // T-10-2101: 純関数。完了していない（Active・AwaitingEntry）・手法が違う・同じ銘柄・市場・方向・自分以外の記録だけを数える。
    [Fact]
    public void T_10_2101_併存してはならない記録の判定()
    {
        var own = Guid.NewGuid();
        var s0Active = Row(StopLossExecutionMethod.BrokerStopOrder);
        var s3Awaiting = Row(StopLossExecutionMethod.AlternativeBrokerOrderType, ProtectiveStopState.AwaitingEntry);
        var s0Completed = Row(StopLossExecutionMethod.BrokerStopOrder, ProtectiveStopState.Completed);
        var s1Same = Row(StopLossExecutionMethod.SoftwareStop);
        var s0Other = Row(StopLossExecutionMethod.BrokerStopOrder, symbol: "MSFT");
        var s0Short = Row(StopLossExecutionMethod.BrokerStopOrder, side: TradeSide.Sell);
        var ownRow = Row(StopLossExecutionMethod.BrokerStopOrder) with { EntryDecisionId = own };

        StopLossMethodCoexistenceGate.Conflicting(
                own, Intent(), StopLossExecutionMethod.SoftwareStop,
                [s0Active, s3Awaiting, s0Completed, s1Same, s0Other, s0Short, ownRow])
            .Should().BeEquivalentTo([s0Active, s3Awaiting], "完了した記録・同じ手法・他銘柄・逆方向・自分の記録は数えない");

        StopLossMethodCoexistenceGate.MechanismOf(StopLossMethodDisposition.NotImplementedFallbackToBrokerStop)
            .Should().Be(StopLossExecutionMethod.BrokerStopOrder, "未実装の手法の読み替えは S0 の記録を作る");
        StopLossMethodCoexistenceGate.MechanismOf(StopLossMethodDisposition.ProtectiveStopWaived)
            .Should().Be(StopLossExecutionMethod.NoProtectiveStop);
        StopLossMethodCoexistenceGate.MechanismOf(StopLossMethodDisposition.Refused).Should().BeNull();
    }

    // T-10-2102: 受け入れ基準 1。S0 の有効な記録がある銘柄へ S2 で新規建て → 理由 StopLossMethodConflict で見送る（送らない）。
    // 対照: 別銘柄・同じ手法（S0→S0）は従来どおり送る。
    [Theory]
    [InlineData(StopLossExecutionMethod.NoProtectiveStop, StopLossExecutionMethod.BrokerStopOrder, true)]
    [InlineData(StopLossExecutionMethod.SoftwareStop, StopLossExecutionMethod.AlternativeBrokerOrderType, true)]
    [InlineData(StopLossExecutionMethod.BrokerStopOrder, StopLossExecutionMethod.AlternativeBrokerOrderType, true)]
    [InlineData(StopLossExecutionMethod.AlternativeBrokerOrderType, StopLossExecutionMethod.BrokerStopOrder, true)]
    [InlineData(StopLossExecutionMethod.NoProtectiveStop, StopLossExecutionMethod.SoftwareStop, true)]
    [InlineData(StopLossExecutionMethod.BrokerStopOrder, StopLossExecutionMethod.BrokerStopOrder, false)]
    [InlineData(StopLossExecutionMethod.SoftwareStop, StopLossExecutionMethod.SoftwareStop, false)]
    public async Task T_10_2102_別の手法の有効な記録がある銘柄では新規建てを見送る(
        StopLossExecutionMethod newMethod, StopLossExecutionMethod existing, bool forgone)
    {
        var broker = new Broker();
        var stops = new InMemoryProtectiveStopOrderStore();
        // 同じ手法の S1 の行は帰属不明の判定で建玉を主張するので、その数量だけ建玉があるとする（帰属不明 0）。
        broker.Positions = [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 5, 1_000m)];
        stops.Save(Row(existing));
        var reservations = new InMemoryOrderReservationStore();
        var service = new AppSvc(broker, new InMemoryExecutedOrderStore(), reservations, new FakeClock(), stops);
        var approved = Approved(newMethod);

        var result = await service.ExecuteAsync(approved);

        if (forgone)
        {
            result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.StopLossMethodConflict);
            broker.PlaceCount.Should().Be(0, "併存させる新規建ては送らない");
            reservations.Find(approved.DecisionId)!.State.Should().Be(OrderDispatchState.Forgone, "見送りを記録して再配送でも送らない");
            stops.Find(approved.DecisionId).Should().BeNull("見送った新規建ての保護記録は作らない");
        }
        else
        {
            result.Forgone.Should().BeNull("同じ手法どうしは従来どおり建てる");
            broker.PlaceCount.Should().Be(1);
        }
    }

    // T-10-2103: 受け入れ基準 2。送信結果待ち（AwaitingEntry）の S0 / S3 の記録も「有効」に数える。完了した記録は数えない。
    [Theory]
    [InlineData(ProtectiveStopState.AwaitingEntry, true)]
    [InlineData(ProtectiveStopState.Completed, false)]
    public async Task T_10_2103_送信結果待ちの記録も有効に数え完了した記録は数えない(ProtectiveStopState state, bool forgone)
    {
        var broker = new Broker();
        var stops = new InMemoryProtectiveStopOrderStore();
        stops.Save(Row(StopLossExecutionMethod.BrokerStopOrder, state));
        var service = new AppSvc(broker, new InMemoryExecutedOrderStore(), new InMemoryOrderReservationStore(), new FakeClock(), stops);

        var result = await service.ExecuteAsync(Approved(StopLossExecutionMethod.NoProtectiveStop));

        (result.Forgone?.Reason == OrderDispatchForgoneReason.StopLossMethodConflict).Should().Be(forgone);
    }

    // T-10-2104: 受け入れ基準 3（決済は止めない）。別の手法の記録がある銘柄でも、手仕舞い（Close）は送る。
    // 照合は方向で絞るので、ロングの手仕舞い（売り）だけでなく、記録と同じ向きになる決済（ショートの買い戻し）でも確かめる。
    [Theory]
    [InlineData(TradeSide.Sell, 10)]
    [InlineData(TradeSide.Buy, -10)]
    public async Task T_10_2104_決済は別の手法の記録があっても止めない(TradeSide closeSide, int held)
    {
        var broker = new Broker { Positions = [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, held, 1_000m)] };
        var stops = new InMemoryProtectiveStopOrderStore();
        stops.Save(Row(StopLossExecutionMethod.SoftwareStop));
        stops.Save(Row(StopLossExecutionMethod.AlternativeBrokerOrderType, side: TradeSide.Sell));
        var service = new AppSvc(broker, new InMemoryExecutedOrderStore(), new InMemoryOrderReservationStore(), new FakeClock(), stops);

        var result = await service.ExecuteAsync(Approved(
            StopLossExecutionMethod.NoProtectiveStop, Intent(side: closeSide, effect: PositionEffect.Close)));

        result.Forgone.Should().BeNull("手仕舞い・損切りは止めない（FR-10）");
        broker.PlaceCount.Should().Be(1);
    }

    // T-10-2105: 受け入れ基準 4（IADR-0481 決定2）。moomoo SIMULATE の S0・S3 の新規建ては、記録の無い建玉（S2・人手）がある銘柄では
    // 見送る（理由 UnattributedPosition）。対照: 記録の無い建玉が無ければ送る。実弾・内蔵 paper の S0 は建玉照会もしない（経路不変）。
    [Theory]
    [InlineData(StopLossExecutionMethod.BrokerStopOrder, BrokerProvider.MoomooSimulate, 10, true, 1)]
    [InlineData(StopLossExecutionMethod.AlternativeBrokerOrderType, BrokerProvider.MoomooSimulate, 10, true, 1)]
    [InlineData(StopLossExecutionMethod.BrokerStopOrder, BrokerProvider.MoomooSimulate, 0, false, 1)]
    [InlineData(StopLossExecutionMethod.BrokerStopOrder, BrokerProvider.MoomooReal, 10, false, 0)]
    [InlineData(StopLossExecutionMethod.BrokerStopOrder, BrokerProvider.InternalPaper, 10, false, 0)]
    public async Task T_10_2105_SIMULATEのS0とS3は記録の無い建玉がある銘柄では見送る(
        StopLossExecutionMethod method, BrokerProvider provider, int heldWithoutRecord, bool forgone, int expectedQueries)
    {
        var broker = new Broker(provider)
        {
            Positions = heldWithoutRecord > 0 ? [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, heldWithoutRecord, 1_000m)] : [],
        };
        var service = new AppSvc(
            broker, new InMemoryExecutedOrderStore(), new InMemoryOrderReservationStore(), new FakeClock(),
            new InMemoryProtectiveStopOrderStore());

        var result = await service.ExecuteAsync(Approved(method, Intent(mode: provider)));

        (result.Forgone?.Reason == OrderDispatchForgoneReason.UnattributedPosition).Should().Be(forgone);
        broker.PositionQueries.Should().Be(expectedQueries, "建玉照会を足すのは moomoo SIMULATE の S0・S3 だけ");
    }

    // T-10-2106: 受け入れ基準 5（窓の後の端・増える側のプローブ）。予約の前の照合を通った後、送信の前に別の手法の記録が現れたら、
    // 送信の直前の照合で見送る。予約は Forgone へ移り、自分の S1 の行は完了になる（建玉を作らない）。
    [Theory]
    [InlineData(StopLossExecutionMethod.SoftwareStop, StopLossExecutionMethod.BrokerStopOrder)]
    [InlineData(StopLossExecutionMethod.BrokerStopOrder, StopLossExecutionMethod.SoftwareStop)]
    [InlineData(StopLossExecutionMethod.NoProtectiveStop, StopLossExecutionMethod.AlternativeBrokerOrderType)]
    public async Task T_10_2106_送信の直前に現れた別の手法の記録でも見送る(
        StopLossExecutionMethod newMethod, StopLossExecutionMethod appearing)
    {
        var broker = new Broker();
        var stops = new InMemoryProtectiveStopOrderStore();
        var appeared = Row(appearing, appearing == StopLossExecutionMethod.SoftwareStop
            ? ProtectiveStopState.Active
            : ProtectiveStopState.AwaitingEntry);
        var reservations = new InjectingReservations(() => stops.Save(appeared));
        var service = new AppSvc(broker, new InMemoryExecutedOrderStore(), reservations, new FakeClock(), stops);
        var approved = Approved(newMethod);

        var result = await service.ExecuteAsync(approved);

        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.StopLossMethodConflict);
        broker.PlaceCount.Should().Be(0, "後の端で見つけたら送らない");
        reservations.Find(approved.DecisionId)!.State.Should().Be(OrderDispatchState.Forgone);
        if (stops.Find(approved.DecisionId) is { } own)
            own.State.Should().Be(ProtectiveStopState.Completed, "見送った新規建ての自分の記録は閉じる（建玉は生じない）");
    }

    // T-10-2107: 受け入れ基準 5（窓の前の端・減る側のプローブ）。予約の前の照合では別の手法の記録があり、送信の前に完了した
    // （＝記録が減った）。前の端で既に見送っており、送らない（後の端だけを見る形では送ってしまう）。
    [Fact]
    public async Task T_10_2107_予約の前に見えた別の手法の記録が送信の前に完了しても見送ったまま()
    {
        var broker = new Broker();
        var stops = new InMemoryProtectiveStopOrderStore();
        var existing = Row(StopLossExecutionMethod.BrokerStopOrder);
        stops.Save(existing);
        var reservations = new InjectingReservations(
            () => stops.Save(stops.Find(existing.EntryDecisionId)! with { State = ProtectiveStopState.Completed }));
        var service = new AppSvc(broker, new InMemoryExecutedOrderStore(), reservations, new FakeClock(), stops);

        var result = await service.ExecuteAsync(Approved(StopLossExecutionMethod.NoProtectiveStop));

        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.StopLossMethodConflict);
        broker.PlaceCount.Should().Be(0);
    }
}
