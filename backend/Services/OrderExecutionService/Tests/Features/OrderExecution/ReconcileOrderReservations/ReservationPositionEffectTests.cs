using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.Persistence;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// FR-10, UC-06, ADR-0050 決定1, #1262, IADR-0515 追記(2): 送る発注の建て・決済の別（PositionEffect）を予約の行
// （order_dispatch_reservations.PositionEffect）にも残し、送信結果が不明のまま突合が発注済みと確定したとき、本番の照会
// （MoomooReservationBrokerProbe。moomoo の注文は建て・決済の別を返さず Open で近似する）が組み直した発注意図ではなく、予約の行の値で記録を書く。
// StopFloorSource（IADR-0486 決定6）・ApprovalOrigin（IADR-0515 追記(1)）と同じ形。列を足す前の行（null）は照会の値のまま（是正前と同じ）。
public class ReservationPositionEffectTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StalledAt = Now.AddHours(-48);
    private static readonly DateTimeOffset Cutoff = Now.AddHours(-24);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private static DbContextOptions<OrderExecutionDbContext> NewDb() =>
        new DbContextOptionsBuilder<OrderExecutionDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    // T-10-2456（受け入れ基準 1・3 の前提）: 予約の行は建て・決済の別を保存して読み戻す（本番の DB 実装〔別のコンテキストから〕・インメモリ。
    // Find・FindStalledReserved）。確定（MarkCompleted）で消えない。省略（列を足す前の行）は null（分からない）。出どころ・印と並んで保たれる。
    [Theory]
    [InlineData("ef")]
    [InlineData("inmemory")]
    public void T_10_2456_予約の行は建て決済の別を保存して読み戻し省略はnullのまま(string kind)
    {
        var options = NewDb();
        var memory = new InMemoryOrderReservationStore();
        Func<IOrderReservationStore> open = kind == "ef"
            ? () => new EfOrderReservationStore(new OrderExecutionDbContext(options))
            : () => memory;
        var close = Guid.NewGuid();
        var entry = Guid.NewGuid();
        var omitted = Guid.NewGuid();
        open().TryReserve(
                close, StalledAt, BrokerProvider.MoomooSimulate, approvalOrigin: OrderApprovalOrigin.OwnerClose,
                positionEffect: PositionEffect.Close)
            .Should().BeTrue();
        open().TryReserve(
                entry, StalledAt.AddSeconds(1), BrokerProvider.MoomooSimulate, StopWidthFloorSource.Atr14,
                OrderApprovalOrigin.TradeDecision, PositionEffect.Open)
            .Should().BeTrue();
        open().TryReserve(omitted, StalledAt.AddSeconds(2), BrokerProvider.MoomooSimulate).Should().BeTrue();

        var stalled = open().FindStalledReserved(Cutoff, 10).ToDictionary(r => r.DecisionId);
        stalled[close].PositionEffect.Should().Be(PositionEffect.Close);
        stalled[close].ApprovalOrigin.Should().Be(OrderApprovalOrigin.OwnerClose, "出どころと並んで保たれる");
        stalled[entry].PositionEffect.Should().Be(PositionEffect.Open, "序数 0 も null と区別して保たれる");
        stalled[entry].StopFloorSource.Should().Be(StopWidthFloorSource.Atr14);
        stalled[omitted].PositionEffect.Should().BeNull("省略は分からない（推測で埋めない）");

        open().MarkCompleted(close, "BRK-1", Now);
        open().Find(close)!.PositionEffect.Should().Be(PositionEffect.Close, "確定で消えない");
        open().Find(omitted)!.PositionEffect.Should().BeNull();
    }

    // T-10-2457（受け入れ基準 1 の前提）: 承認の経路の発注は、送る前に発注意図の建て・決済の別を予約の行へ残す（相 4 の記録と同じ値）。
    // 利用者の手仕舞い・維持率割れの自動縮小・判断の手仕舞い（Close）も、エントリー（Open）も。保護の機構の 5 つの書き手（Close）は
    // それぞれの経路の既存の試験で確かめる（T-10-2457 の注記のある行）。
    [Theory]
    [InlineData(OrderApprovalOrigin.OwnerClose, PositionEffect.Close)]
    [InlineData(OrderApprovalOrigin.MaintenanceMarginReduction, PositionEffect.Close)]
    [InlineData(OrderApprovalOrigin.TradeDecision, PositionEffect.Close)]
    [InlineData(OrderApprovalOrigin.TradeDecision, PositionEffect.Open)]
    public async Task T_10_2457_承認の発注は建て決済の別を予約の行に残す(OrderApprovalOrigin origin, PositionEffect effect)
    {
        var reservations = new InMemoryOrderReservationStore();
        var store = new InMemoryExecutedOrderStore();
        var service = new AppSvc(new PaperBrokerAdapter(), store, reservations, new FakeClock());
        var intent = effect == PositionEffect.Open
            ? new OrderIntent(
                "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 100m,
                PositionEffect.Open, StopLossPrice: 98m)
            : new OrderIntent(
                "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper, 10, 100m,
                PositionEffect.Close, StopLossPrice: null);
        var approved = new OrderApproved(Guid.NewGuid(), intent, 10, Now, Origin: origin);

        await service.ExecuteAsync(approved);

        reservations.Find(approved.DecisionId)!.PositionEffect.Should().Be(effect);
        store.FindByDecisionId(approved.DecisionId)!.PositionEffect.Should().Be(effect, "予約の行と発注の記録は同じ値を持つ");
    }

    // 送信結果が不明のまま滞留した予約（決済の売り 10 株）を、本番の照会の写像（MoomooReservationBrokerProbe）で発注済みと確定させる。
    private static async Task<ExecutionRecord> ReconcileViaMoomooProbe(
        IOrderReservationStore reservations, IExecutedOrderStore executed, OrderApprovalOrigin? origin, PositionEffect? reservedEffect,
        MoomooSide side = MoomooSide.Sell)
    {
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(
                decisionId, StalledAt, BrokerProvider.MoomooSimulate, approvalOrigin: origin, positionEffect: reservedEffect)
            .Should().BeTrue();
        var client = new FoundOrderMoomooClient(new MoomooOrderSnapshot(
            $"mo-{decisionId:N}", MoomooOrderState.Submitted, "AAPL", MoomooMarket.UnitedStates, side,
            Quantity: 10, Price: 100m, FilledQuantity: 0, AveragePrice: 0m, PlacedAt: StalledAt, CompletedAt: null));
        var reconciler = new OrderReservationReconciler(
            reservations, executed, new MoomooReservationBrokerProbe(client), new ProviderOverrideBroker(BrokerProvider.MoomooSimulate),
            new FakeClock(), Options.Create(new ReconciliationOptions { Enabled = true }));

        (await reconciler.ReconcileAsync(Cutoff, batchSize: 10)).Terminalized.Should().Be(1);
        return executed.FindByDecisionId(decisionId)!;
    }

    // T-10-2458（受け入れ基準 1）: 🔴 送信結果が不明のまま突合で確定した決済（利用者の手仕舞い・自動縮小・判断の手仕舞い・保護の機構の決済〔出どころ null〕）は、
    // 本番の照会の写像が Open を返しても、予約の行の値（Close）で記録され、処理中の決済の読み出し（FindPendingCloses）に載る（インメモリ・EF。EF は別のコンテキストから）。
    [Theory]
    [InlineData("inmemory", OrderApprovalOrigin.OwnerClose)]
    [InlineData("ef", OrderApprovalOrigin.OwnerClose)]
    [InlineData("ef", OrderApprovalOrigin.MaintenanceMarginReduction)]
    [InlineData("inmemory", OrderApprovalOrigin.TradeDecision)]
    [InlineData("ef", null)]
    public async Task T_10_2458_突合で確定した決済は本番の照会の写像を通しても決済として記録される(string kind, OrderApprovalOrigin? origin)
    {
        var options = NewDb();
        using var db = new OrderExecutionDbContext(options);
        IOrderReservationStore reservations = kind == "ef" ? new EfOrderReservationStore(db) : new InMemoryOrderReservationStore();
        IExecutedOrderStore executed = kind == "ef" ? new EfExecutedOrderStore(db) : new InMemoryExecutedOrderStore();

        var record = await ReconcileViaMoomooProbe(reservations, executed, origin, PositionEffect.Close);

        record.PositionEffect.Should().Be(PositionEffect.Close, "照会は Open で近似するが、予約の行が送った時の値を持つ");
        record.ApprovalOrigin.Should().Be(origin);
        record.Side.Should().Be(TradeSide.Sell);
        IExecutedOrderStore reader = kind == "ef" ? new EfExecutedOrderStore(new OrderExecutionDbContext(options)) : executed;
        reader.FindPendingCloses("AAPL", Market.UnitedStates, TradeSide.Sell)
            .Should().ContainSingle().Which.DecisionId.Should().Be(record.DecisionId);
        reader.FindRecentOpens("AAPL", Market.UnitedStates, TradeSide.Sell, 10).Should().BeEmpty("新規建てとしては残らない");
    }

    // T-10-2460（受け入れ基準 3・否定形）: 🔴 列を足す前の予約の行（null）は、照会の値（本番では Open）のまま記録する＝是正前と同じ
    // （推測で決済にしない）。エントリー（予約の行が Open）は Open のまま、決済の読み出しに載らない。照会が決済を返す試験の照会でも、
    // 予約の行が null なら照会の値のまま（Close）、予約の行が Open なら予約の行の値（Open）が勝つ。
    [Theory]
    [InlineData("inmemory", null, PositionEffect.Open)]
    [InlineData("ef", null, PositionEffect.Open)]
    [InlineData("ef", PositionEffect.Open, PositionEffect.Open)]
    public async Task T_10_2460_列を足す前の予約の行とエントリーは照会の値や新規建てのまま記録される(
        string kind, PositionEffect? reservedEffect, PositionEffect expected)
    {
        var options = NewDb();
        using var db = new OrderExecutionDbContext(options);
        IOrderReservationStore reservations = kind == "ef" ? new EfOrderReservationStore(db) : new InMemoryOrderReservationStore();
        IExecutedOrderStore executed = kind == "ef" ? new EfExecutedOrderStore(db) : new InMemoryExecutedOrderStore();

        var record = await ReconcileViaMoomooProbe(
            reservations, executed, OrderApprovalOrigin.OwnerClose, reservedEffect,
            side: reservedEffect == PositionEffect.Open ? MoomooSide.Buy : MoomooSide.Sell);

        record.PositionEffect.Should().Be(expected);
        executed.FindPendingCloses("AAPL", Market.UnitedStates, record.Side).Should().BeEmpty("決済として数えない（是正前と同じ）");
    }

    [Theory]
    [InlineData(null, PositionEffect.Close)]
    [InlineData(PositionEffect.Open, PositionEffect.Open)]
    public async Task T_10_2460_予約の行が無ければ照会の値を使い在れば予約の行の値が勝つ(PositionEffect? reservedEffect, PositionEffect expected)
    {
        var reservations = new InMemoryOrderReservationStore();
        var executed = new InMemoryExecutedOrderStore();
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt, BrokerProvider.MoomooSimulate, positionEffect: reservedEffect).Should().BeTrue();
        var probedClose = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 100m, PositionEffect.Close,
            StopLossPrice: null);
        var reconciler = new OrderReservationReconciler(
            reservations, executed, new PlacedProbe(new BrokerOrder("BRK-7", probedClose, OrderStatus.Accepted, 0, 0m, StalledAt, null)),
            new ProviderOverrideBroker(BrokerProvider.MoomooSimulate), new FakeClock(),
            Options.Create(new ReconciliationOptions { Enabled = true }));

        await reconciler.ReconcileAsync(Cutoff, batchSize: 10);

        executed.FindByDecisionId(decisionId)!.PositionEffect.Should().Be(expected);
    }

    private sealed class PlacedProbe(BrokerOrder order) : IReservationBrokerProbe
    {
        public Task<ReservationProbeResult> ProbeAsync(
            OrderDispatchReservation reservation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReservationProbeResult.Placed(order));
    }
}

// #1262: 予約の DecisionId（remark）の照会に、与えた注文のスナップショットを返す moomoo の口（本番の照会 MoomooReservationBrokerProbe の写像を通すため）。
// 他の口は予約の突合では使わない。
internal sealed class FoundOrderMoomooClient(MoomooOrderSnapshot snapshot) : IMoomooTradeClient
{
    public Task<MoomooOrderSnapshot?> FindOrderByClientIdAsync(
        string clientOrderId, DateTimeOffset reservedAtUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult<MoomooOrderSnapshot?>(snapshot);

    public Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<MoomooPositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<MoomooAccountType?> GetAccountTypeAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<decimal?> GetAccountEquityInBaseAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
