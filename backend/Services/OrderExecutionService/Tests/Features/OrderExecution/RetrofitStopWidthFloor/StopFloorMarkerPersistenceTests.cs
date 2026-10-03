using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;
using OrderExecutionService.Infrastructure.Persistence;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// FR-10, ADR-0049 決定1, #1122（オーナー裁定 2026-10-03・案 A）, IADR-0486 決定6: 承認の発注意図が運ぶ「損切り幅に下限を掛けてラインを引いた」印
// （OrderIntent.StopFloorSource）を、発注執行は発注結果の記録（executed_orders）と予約の行（order_dispatch_reservations）に残す。
// 送信結果が不明のまま突合が発注済みと確定したときも、予約の行の印を組み直した記録へ写す（予約の経路を閉じる）。
public class StopFloorMarkerPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StalledAt = Now.AddHours(-48);
    private static readonly DateTimeOffset Cutoff = Now.AddHours(-24);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private static OrderIntent Entry(StopWidthFloorSource? source) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 100m, PositionEffect.Open,
            StopLossPrice: 98.8m, StopFloorSource: source);

    private static DbContextOptions<OrderExecutionDbContext> NewDb() =>
        new DbContextOptionsBuilder<OrderExecutionDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    // T-10-2203, IADR-0486 決定6: 通常の発注の経路は、承認の発注意図の印を予約の行（送る前）と発注結果の記録（送った後）の両方に残す。
    // 印の無い承認（決済・#1122 より前の判断）は null のまま。
    [Theory]
    [InlineData(StopWidthFloorSource.Atr14)]
    [InlineData(StopWidthFloorSource.Fallback2Pct)]
    [InlineData(null)]
    public async Task T_10_2203_発注は印を予約の行と発注結果の記録に残す(StopWidthFloorSource? source)
    {
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var service = new AppSvc(new PaperBrokerAdapter(), store, reservations, new FakeClock());
        var approved = new OrderApproved(Guid.NewGuid(), Entry(source), 10, Now);

        await service.ExecuteAsync(approved);

        store.FindByDecisionId(approved.DecisionId)!.StopFloorSource.Should().Be(source);
        reservations.Find(approved.DecisionId)!.StopFloorSource.Should().Be(source);
    }

    // T-10-2203: 本番の DB 実装（EF）でも、列（StopFloorSource）に書いて別のコンテキストから同じ値で読める（記録・予約の両方）。
    [Fact]
    public void T_10_2203_EFの記録と予約は印を保存して読み戻す()
    {
        var options = NewDb();
        var decisionId = Guid.NewGuid();
        using (var db = new OrderExecutionDbContext(options))
        {
            new EfExecutedOrderStore(db).Save(new ExecutionRecord(
                decisionId, "BRK-1", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, PositionEffect.Open,
                10, 100m, 10, 100m, OrderStatus.Filled, 0m, Now, StopWidthFloorSource.Atr14));
            new EfOrderReservationStore(db).TryReserve(decisionId, StalledAt, BrokerProvider.MoomooSimulate, StopWidthFloorSource.Atr14)
                .Should().BeTrue();
            var other = Guid.NewGuid();
            new EfOrderReservationStore(db).TryReserve(other, StalledAt, BrokerProvider.MoomooSimulate).Should().BeTrue();
        }

        using var read = new OrderExecutionDbContext(options);
        new EfExecutedOrderStore(read).FindByDecisionId(decisionId)!.StopFloorSource.Should().Be(StopWidthFloorSource.Atr14);
        var reservationStore = new EfOrderReservationStore(read);
        reservationStore.Find(decisionId)!.StopFloorSource.Should().Be(StopWidthFloorSource.Atr14);
        reservationStore.FindStalledReserved(Cutoff, 10).Should().HaveCount(2)
            .And.ContainSingle(r => r.DecisionId == decisionId && r.StopFloorSource == StopWidthFloorSource.Atr14);
        reservationStore.FindStalledReserved(Cutoff, 10).Where(r => r.DecisionId != decisionId)
            .Should().ContainSingle().Which.StopFloorSource.Should().BeNull("省略は null（分からない）");
    }

    // T-10-2204, IADR-0486 決定6: 🔴 予約の経路。送信結果が不明のまま滞留した予約を、突合がブローカーの照会で発注済みと確定したとき、
    // ブローカーの注文（印を持たない）から組み直す記録へ、予約の行が残した印を写す（インメモリ・EF の両方）。
    [Theory]
    [InlineData("inmemory", StopWidthFloorSource.Atr14)]
    [InlineData("ef", StopWidthFloorSource.Atr14)]
    [InlineData("ef", StopWidthFloorSource.Fallback2Pct)]
    [InlineData("inmemory", null)]
    public async Task T_10_2204_突合で確定した記録は予約の行の印を持つ(string kind, StopWidthFloorSource? source)
    {
        var options = NewDb();
        using var db = new OrderExecutionDbContext(options);
        IOrderReservationStore reservations = kind == "ef" ? new EfOrderReservationStore(db) : new InMemoryOrderReservationStore();
        IExecutedOrderStore executed = kind == "ef" ? new EfExecutedOrderStore(db) : new InMemoryExecutedOrderStore();
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt, BrokerProvider.MoomooSimulate, source);
        // ブローカーの注文の発注意図は印を持たない（照会の結果から組み直す）。
        var brokerOrder = new BrokerOrder("BRK-9", Entry(source: null), OrderStatus.Filled, 10, 100m, StalledAt, StalledAt);
        var reconciler = new OrderReservationReconciler(
            reservations, executed, new PlacedProbe(brokerOrder), new ProviderOverrideBroker(BrokerProvider.MoomooSimulate),
            new FakeClock(),
            Options.Create(new ReconciliationOptions { Enabled = true }));

        var result = await reconciler.ReconcileAsync(Cutoff, batchSize: 10);

        result.Terminalized.Should().Be(1);
        executed.FindByDecisionId(decisionId)!.StopFloorSource.Should().Be(source);
        using var read = new OrderExecutionDbContext(options);
        if (kind == "ef")
            new EfExecutedOrderStore(read).FindByDecisionId(decisionId)!.StopFloorSource.Should().Be(source);
    }

    private sealed class PlacedProbe(BrokerOrder order) : IReservationBrokerProbe
    {
        public Task<ReservationProbeResult> ProbeAsync(
            OrderDispatchReservation reservation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReservationProbeResult.Placed(order));
    }
}
