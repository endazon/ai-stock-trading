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

// FR-10, FR-12, ADR-0040 決定1, #819, IADR-0342: 損切りの実行機構（S0〜S3）を承認から受け取ったときの発注執行の分岐。
// 受け入れ基準:
//   2b. 実弾（SIMULATE 以外）で S0 以外が有効な承認は発注されない
//   3.  SIMULATE＋S2 で新規買いが保護レグなしで建玉として残り、免除の事実が発行される
//   4.  空売りは S2 でも S0 と同じ扱い
//   5.  S1/S3 は未実装のため S0 と同じ扱い
public class OrderExecutionServiceStopLossMethodTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 14, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class ScriptedBroker(BrokerProvider provider) : IBrokerAdapter, IProtectiveOrderBroker
    {
        public BrokerProvider Provider { get; } = provider;

        public OrderStatus EntryStatus { get; init; } = OrderStatus.Filled;
        public bool RejectStop { get; init; } = true; // moomoo SIMULATE の実測（Stop 注文を受け付けない）が既定

        public int PlaceCount { get; private set; }
        public int StopPlaceCount { get; private set; }
        public int CancelCount { get; private set; }
        public int MarketCloseCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            PlaceCount++;
            var filled = EntryStatus == OrderStatus.Filled ? intent.Quantity : 0;
            return Task.FromResult(new BrokerOrder(
                "entry-1", intent, EntryStatus, filled, filled > 0 ? intent.Price : 0m, Now,
                EntryStatus == OrderStatus.Filled ? Now : null));
        }

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default)
        {
            StopPlaceCount++;
            return Task.FromResult(new BrokerOrder(
                "stop-1", closeIntent, RejectStop ? OrderStatus.Rejected : OrderStatus.Accepted, 0, 0m, Now,
                RejectStop ? Now : null));
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

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            CancelCount++;
            return Task.CompletedTask;
        }
    }

    private static OrderIntent LongEntry(ProductType productType = ProductType.Cash) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, productType, BrokerProvider.MoomooSimulate, 10, 1_000m,
            PositionEffect.Open, StopLossPrice: 950m, FxRateToBase: 1m);

    private static OrderIntent ShortEntry() =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell, BrokerProvider.MoomooSimulate, 10,
            1_000m, PositionEffect.Open, StopLossPrice: 1_050m, FxRateToBase: 1m);

    private static (AppSvc Service, InMemoryExecutedOrderStore Store, InMemoryProtectiveStopOrderStore Stops,
        InMemoryOrderReservationStore Reservations) NewService(IBrokerAdapter broker)
    {
        var store = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        return (new AppSvc(broker, store, reservations, new FakeClock(), stops), store, stops, reservations);
    }

    private static OrderApproved Approved(OrderIntent intent, StopLossExecutionMethod method) =>
        new(Guid.NewGuid(), intent, intent.Quantity, Now, StopLossMethod: method);

    // ---- 受け入れ基準 3: SIMULATE＋S2 ----

    [Theory]
    [InlineData(ProductType.Cash, OrderStatus.Filled)]
    [InlineData(ProductType.Cash, OrderStatus.Accepted)]
    [InlineData(ProductType.Cash, OrderStatus.PartiallyFilled)]
    [InlineData(ProductType.MarginLong, OrderStatus.Filled)]
    public async Task SIMULATEでS2の新規買いは保護レグなしで建玉として残り免除の事実が出る(
        ProductType productType, OrderStatus entryStatus)
    {
        var broker = new ScriptedBroker(BrokerProvider.MoomooSimulate) { EntryStatus = entryStatus };
        var (service, store, stops, _) = NewService(broker);
        var approved = Approved(LongEntry(productType), StopLossExecutionMethod.NoProtectiveStop);

        var result = await service.ExecuteAsync(approved);

        broker.PlaceCount.Should().Be(1, "エントリーは発注する");
        broker.StopPlaceCount.Should().Be(0, "S2 は保護逆指値を発注しない");
        broker.CancelCount.Should().Be(0, "S2 はエントリーを取り消さない");
        broker.MarketCloseCount.Should().Be(0, "S2 は建玉を手仕舞わない");
        result.Executed!.Status.Should().Be(entryStatus);
        result.StopPlaced.Should().BeNull();
        result.CoverageLost.Should().BeNull("免除は保護喪失（統制が働いた・破れた記録）ではない");

        var waived = result.StopWaived!;
        waived.EntryDecisionId.Should().Be(approved.DecisionId);
        waived.Symbol.Should().Be("AAPL");
        waived.Side.Should().Be(TradeSide.Buy);
        waived.ProductType.Should().Be(productType);
        waived.Quantity.Should().Be(10);
        waived.StopLossPrice.Should().Be(950m);
        waived.Method.Should().Be(StopLossExecutionMethod.NoProtectiveStop);
        waived.Provider.Should().Be(BrokerProvider.MoomooSimulate);
        waived.OccurredAt.Should().Be(Now);

        // ProtectiveStopGuard の巡回対象（protective_stop_orders）へ入らない＝失効扱いで手仕舞われない。
        stops.Find(approved.DecisionId).Should().BeNull();
        stops.FindActive(100).Should().BeEmpty();
        store.GetAll().Should().ContainSingle(r => r.DecisionId == approved.DecisionId, "エントリーだけが記録される");
    }

    [Theory]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.Cancelled)]
    public async Task S2でもエントリーが生きていなければ免除の事実は出ない(OrderStatus entryStatus)
    {
        var broker = new ScriptedBroker(BrokerProvider.MoomooSimulate) { EntryStatus = entryStatus };
        var (service, _, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(LongEntry(), StopLossExecutionMethod.NoProtectiveStop));

        result.StopWaived.Should().BeNull("建玉が生じない注文に免除は無い");
        broker.StopPlaceCount.Should().Be(0);
    }

    // ---- 受け入れ基準 2b: SIMULATE 以外で S0 以外 → 発注しない ----

    [Theory]
    [InlineData(BrokerProvider.MoomooReal, StopLossExecutionMethod.NoProtectiveStop)]
    [InlineData(BrokerProvider.MoomooReal, StopLossExecutionMethod.SoftwareStop)]
    [InlineData(BrokerProvider.MoomooReal, StopLossExecutionMethod.AlternativeBrokerOrderType)]
    [InlineData(BrokerProvider.InternalPaper, StopLossExecutionMethod.NoProtectiveStop)]
    public async Task SIMULATE以外でS0以外の承認は発注せず見送る(BrokerProvider provider, StopLossExecutionMethod method)
    {
        var broker = new ScriptedBroker(provider);
        var (service, store, stops, reservations) = NewService(broker);
        var approved = Approved(LongEntry(), method);

        var result = await service.ExecuteAsync(approved);

        broker.PlaceCount.Should().Be(0, "実弾で無防備な建玉を作らない（ADR-0040 決定1）");
        broker.StopPlaceCount.Should().Be(0);
        result.Executed.Should().BeNull();
        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.StopLossMethodNotPermitted);
        result.Forgone.DecisionId.Should().Be(approved.DecisionId);
        store.GetAll().Should().BeEmpty();
        stops.FindActive(100).Should().BeEmpty();
        reservations.Find(approved.DecisionId).Should().BeNull("発注に着手しないため予約も取らない");
    }

    // 空売りでも実弾の拒否が先に効く（S0 以外の設定が有効なこと自体が不正な状態である）。
    [Fact]
    public async Task 実弾では空売りでもS0以外の承認は発注しない()
    {
        var broker = new ScriptedBroker(BrokerProvider.MoomooReal);
        var (service, _, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(ShortEntry(), StopLossExecutionMethod.NoProtectiveStop));

        broker.PlaceCount.Should().Be(0);
        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.StopLossMethodNotPermitted);
    }

    // ---- 受け入れ基準 4: 空売りは S2 でも S0 ----

    [Fact]
    public async Task 空売りはS2でもS0と同じく逆指値を発注し未受理なら建玉を解消する()
    {
        var broker = new ScriptedBroker(BrokerProvider.MoomooSimulate) { RejectStop = true };
        var (service, _, _, _) = NewService(broker);
        var approved = Approved(ShortEntry(), StopLossExecutionMethod.NoProtectiveStop);

        var result = await service.ExecuteAsync(approved);

        broker.StopPlaceCount.Should().Be(1, "空売りには ADR-0016 決定2(b) が独立に効く");
        result.StopWaived.Should().BeNull();
        var lost = result.CoverageLost!;
        lost.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed, "約定済みのため成行で買い戻す");
        broker.MarketCloseCount.Should().Be(1);
    }

    [Fact]
    public async Task 空売りはS2でも逆指値が受理されれば保護記録が作られる()
    {
        var broker = new ScriptedBroker(BrokerProvider.MoomooSimulate) { RejectStop = false };
        var (service, _, stops, _) = NewService(broker);
        var approved = Approved(ShortEntry(), StopLossExecutionMethod.NoProtectiveStop);

        var result = await service.ExecuteAsync(approved);

        result.StopPlaced!.CloseIntent.Side.Should().Be(TradeSide.Buy);
        result.StopWaived.Should().BeNull();
        stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    // ---- 受け入れ基準 5: S1 / S3 は未実装のため S0 ----

    [Theory]
    [InlineData(StopLossExecutionMethod.SoftwareStop)]
    [InlineData(StopLossExecutionMethod.AlternativeBrokerOrderType)]
    [InlineData((StopLossExecutionMethod)99)]
    public async Task S1とS3と未知の手法はSIMULATEでS0と同じ扱いになる(StopLossExecutionMethod method)
    {
        var broker = new ScriptedBroker(BrokerProvider.MoomooSimulate) { EntryStatus = OrderStatus.Accepted };
        var (service, _, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(LongEntry(), method));

        broker.StopPlaceCount.Should().Be(1, "未実装の手法は緩い側（免除）へ倒さず S0 と同じく逆指値を発注する");
        result.StopWaived.Should().BeNull();
        result.CoverageLost!.Remediation.Should().Be(
            ProtectiveStopRemediation.EntryCancelled, "SIMULATE では逆指値が拒否され、未約定のエントリーは取り消される");
        broker.CancelCount.Should().Be(1);
    }

    // ---- 受け入れ基準 1: S0 は発注先を問わず現行挙動 ----

    [Theory]
    [InlineData(BrokerProvider.MoomooSimulate)]
    [InlineData(BrokerProvider.MoomooReal)]
    [InlineData(BrokerProvider.InternalPaper)]
    public async Task S0は発注先を問わず保護逆指値を同時発注する(BrokerProvider provider)
    {
        var broker = new ScriptedBroker(provider) { RejectStop = false };
        var (service, _, _, _) = NewService(broker);

        var result = await service.ExecuteAsync(Approved(LongEntry(), StopLossExecutionMethod.BrokerStopOrder));

        broker.StopPlaceCount.Should().Be(1);
        result.StopPlaced.Should().NotBeNull();
        result.StopWaived.Should().BeNull();
        result.Forgone.Should().BeNull();
    }

    // 手法は Open にだけ効く。Close（owner 手仕舞い等）は手法に関わらず発注され、保護レグも免除の事実も持たない。
    [Theory]
    [InlineData(BrokerProvider.MoomooSimulate)]
    [InlineData(BrokerProvider.MoomooReal)]
    public async Task 手仕舞いは手法に関わらず発注され免除の事実も出ない(BrokerProvider provider)
    {
        var broker = new ScriptedBroker(provider);
        var (service, _, _, _) = NewService(broker);
        var close = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 10, 1_000m, PositionEffect.Close);

        var result = await service.ExecuteAsync(Approved(close, StopLossExecutionMethod.NoProtectiveStop));

        broker.PlaceCount.Should().Be(1, "手仕舞いは統制で止めない（FR-10）");
        result.Forgone.Should().BeNull();
        result.StopWaived.Should().BeNull();
        broker.StopPlaceCount.Should().Be(0);
    }

    // 再配送（相 1 の完了済み再発行）は免除の事実を再発行しない（保護レグのイベントと同じ規律）。
    [Fact]
    public async Task S2の再配送は発注も免除の事実も重ねない()
    {
        var broker = new ScriptedBroker(BrokerProvider.MoomooSimulate);
        var (service, _, _, _) = NewService(broker);
        var approved = Approved(LongEntry(), StopLossExecutionMethod.NoProtectiveStop);

        (await service.ExecuteAsync(approved)).StopWaived.Should().NotBeNull();
        var second = await service.ExecuteAsync(approved);

        broker.PlaceCount.Should().Be(1);
        second.StopWaived.Should().BeNull();
        second.Executed.Should().NotBeNull();
    }
}
