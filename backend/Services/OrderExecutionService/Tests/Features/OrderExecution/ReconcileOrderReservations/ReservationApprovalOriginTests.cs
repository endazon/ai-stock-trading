using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;
using OrderExecutionService.Infrastructure.Persistence;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// FR-10, UC-06, ADR-0050 決定1, #1253, IADR-0515 追記(1): 承認の出どころ（OrderApproved.Origin）を予約の行（order_dispatch_reservations.ApprovalOrigin）にも
// 残し、送信結果が不明のまま突合が発注済みと確定したとき、ブローカーの注文（出どころを持たない）から組み直す発注の記録へ写す。
// StopFloorSource の前例（IADR-0486 決定6・StopFloorMarkerPersistenceTests）と同じ形。列を足す前の行・出どころの無い承認は null のまま。
public class ReservationApprovalOriginTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StalledAt = Now.AddHours(-48);
    private static readonly DateTimeOffset Cutoff = Now.AddHours(-24);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private static OrderIntent OwnerCloseIntent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 100m, PositionEffect.Close,
            StopLossPrice: null);

    private static DbContextOptions<OrderExecutionDbContext> NewDb() =>
        new DbContextOptionsBuilder<OrderExecutionDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    // T-10-2452（受け入れ基準 3）: 予約の行は承認の出どころを保存して読み戻す（本番の DB 実装〔別のコンテキストから〕・インメモリ。Find・FindStalledReserved）。
    // 確定（MarkCompleted）で消えない。省略（保護の機構の予約・列を足す前の行）は null（分からない）。
    [Theory]
    [InlineData("ef")]
    [InlineData("inmemory")]
    public void T_10_2452_予約の行は承認の出どころを保存して読み戻し省略はnullのまま(string kind)
    {
        var options = NewDb();
        var memory = new InMemoryOrderReservationStore();
        Func<IOrderReservationStore> open = kind == "ef"
            ? () => new EfOrderReservationStore(new OrderExecutionDbContext(options))
            : () => memory;
        var owner = Guid.NewGuid();
        var reduction = Guid.NewGuid();
        var decision = Guid.NewGuid();
        var omitted = Guid.NewGuid();
        open().TryReserve(owner, StalledAt, BrokerProvider.MoomooSimulate, approvalOrigin: OrderApprovalOrigin.OwnerClose)
            .Should().BeTrue();
        open().TryReserve(
                reduction, StalledAt.AddSeconds(1), BrokerProvider.MoomooSimulate,
                approvalOrigin: OrderApprovalOrigin.MaintenanceMarginReduction)
            .Should().BeTrue();
        open().TryReserve(
                decision, StalledAt.AddSeconds(2), BrokerProvider.MoomooSimulate, StopWidthFloorSource.Atr14,
                OrderApprovalOrigin.TradeDecision)
            .Should().BeTrue();
        open().TryReserve(omitted, StalledAt.AddSeconds(3), BrokerProvider.MoomooSimulate).Should().BeTrue();

        var stalled = open().FindStalledReserved(Cutoff, 10).ToDictionary(r => r.DecisionId);
        stalled[owner].ApprovalOrigin.Should().Be(OrderApprovalOrigin.OwnerClose);
        stalled[reduction].ApprovalOrigin.Should().Be(OrderApprovalOrigin.MaintenanceMarginReduction);
        stalled[decision].ApprovalOrigin.Should().Be(OrderApprovalOrigin.TradeDecision);
        stalled[decision].StopFloorSource.Should().Be(StopWidthFloorSource.Atr14, "前例の印と並んで保たれる");
        stalled[omitted].ApprovalOrigin.Should().BeNull("省略は分からない（推測で埋めない）");

        open().MarkCompleted(owner, "BRK-1", Now);
        open().Find(owner)!.ApprovalOrigin.Should().Be(OrderApprovalOrigin.OwnerClose, "確定で消えない");
        open().Find(omitted)!.ApprovalOrigin.Should().BeNull();
    }

    // T-10-2452: 承認の経路の発注は、送る前に承認の出どころを予約の行へ残す。Unknown（旧いメッセージ・書き手の渡し忘れ）は null で書く（発注の記録と同じ）。
    [Theory]
    [InlineData(OrderApprovalOrigin.TradeDecision, OrderApprovalOrigin.TradeDecision)]
    [InlineData(OrderApprovalOrigin.OwnerClose, OrderApprovalOrigin.OwnerClose)]
    [InlineData(OrderApprovalOrigin.MaintenanceMarginReduction, OrderApprovalOrigin.MaintenanceMarginReduction)]
    [InlineData(OrderApprovalOrigin.Unknown, null)]
    public async Task T_10_2452_発注は承認の出どころを予約の行に残す(OrderApprovalOrigin origin, OrderApprovalOrigin? expected)
    {
        var reservations = new InMemoryOrderReservationStore();
        var store = new InMemoryExecutedOrderStore();
        var service = new AppSvc(new PaperBrokerAdapter(), store, reservations, new FakeClock());
        var entry = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 100m, PositionEffect.Open,
            StopLossPrice: 98m);
        var approved = new OrderApproved(Guid.NewGuid(), entry, 10, Now, Origin: origin);

        await service.ExecuteAsync(approved);

        reservations.Find(approved.DecisionId)!.ApprovalOrigin.Should().Be(expected);
        store.FindByDecisionId(approved.DecisionId)!.ApprovalOrigin.Should().Be(expected, "予約の行と発注の記録は同じ値を持つ");
    }

    // T-10-2453（受け入れ基準 1・2・3）: 🔴 送信結果が不明のまま滞留した予約を、突合がブローカーの照会で発注済みと確定したとき、ブローカーの注文
    // （出どころを持たない）から組み直す記録へ、予約の行が残した出どころを写す（インメモリ・EF の両方。EF は別のコンテキストから読み戻す）。
    // 否定形: 出どころの無い予約（列を足す前の行）は null のまま（推測で埋めない）。
    [Theory]
    [InlineData("inmemory", OrderApprovalOrigin.OwnerClose)]
    [InlineData("ef", OrderApprovalOrigin.OwnerClose)]
    [InlineData("ef", OrderApprovalOrigin.MaintenanceMarginReduction)]
    [InlineData("inmemory", OrderApprovalOrigin.TradeDecision)]
    [InlineData("inmemory", null)]
    [InlineData("ef", null)]
    public async Task T_10_2453_突合で確定した記録は予約の行の出どころを持つ(string kind, OrderApprovalOrigin? origin)
    {
        var options = NewDb();
        using var db = new OrderExecutionDbContext(options);
        IOrderReservationStore reservations = kind == "ef" ? new EfOrderReservationStore(db) : new InMemoryOrderReservationStore();
        IExecutedOrderStore executed = kind == "ef" ? new EfExecutedOrderStore(db) : new InMemoryExecutedOrderStore();
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt, BrokerProvider.MoomooSimulate, approvalOrigin: origin).Should().BeTrue();
        var brokerOrder = new BrokerOrder("BRK-9", OwnerCloseIntent(), OrderStatus.Accepted, 0, 0m, StalledAt, null);
        var reconciler = new OrderReservationReconciler(
            reservations, executed, new PlacedProbe(brokerOrder), new ProviderOverrideBroker(BrokerProvider.MoomooSimulate),
            new FakeClock(), Options.Create(new ReconciliationOptions { Enabled = true }));

        var result = await reconciler.ReconcileAsync(Cutoff, batchSize: 10);

        result.Terminalized.Should().Be(1);
        executed.FindByDecisionId(decisionId)!.ApprovalOrigin.Should().Be(origin);
        if (kind == "ef")
        {
            using var read = new OrderExecutionDbContext(options);
            new EfExecutedOrderStore(read).FindPendingCloses("AAPL", Market.UnitedStates, TradeSide.Sell)
                .Should().ContainSingle().Which.ApprovalOrigin.Should().Be(origin);
        }
    }

    private sealed class PlacedProbe(BrokerOrder order) : IReservationBrokerProbe
    {
        public Task<ReservationProbeResult> ProbeAsync(
            OrderDispatchReservation reservation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReservationProbeResult.Placed(order));
    }
}
