using AiStockTrading.OrderExecution.Application.Adapters;
using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Application.Reconciliation;
using AiStockTrading.OrderExecution.Application.Services;
using AiStockTrading.OrderExecution.Worker.Composable.Reconciliation;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiStockTrading.OrderExecution.Worker.Tests;

// #141, FR-05, IADR-0074: 自動リコンサイルの定期実行。既定無効・終端化した予約の OrderExecuted 発行・
// fail-safe（不確定は発行しない）を MassTransit テストハーネスで検証する。
public class OrderReservationReconciliationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StalledAt = Now.AddHours(-48);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class StubProbe(ReservationProbeResult result) : IReservationBrokerProbe
    {
        public Task<ReservationProbeResult> ProbeAsync(
            OrderDispatchReservation reservation, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private static BrokerOrder Placed(string orderId) =>
        new(orderId,
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, 10, 100m),
            OrderStatus.Filled, FilledQuantity: 10, AveragePrice: 100m, PlacedAt: StalledAt, CompletedAt: StalledAt);

    private static ServiceProvider BuildProvider(
        IReservationBrokerProbe probe, InMemoryOrderReservationStore reservations) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock, FakeClock>()
            .AddSingleton<IOrderReservationStore>(reservations)
            .AddSingleton<IExecutedOrderStore, InMemoryExecutedOrderStore>()
            .AddSingleton(probe)
            .AddScoped<OrderReservationReconciler>()
            .AddMassTransitTestHarness()
            .BuildServiceProvider(true);

    private static OrderReservationReconciliationService BuildService(
        ServiceProvider provider, ReconciliationOptions options) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IBus>(),
            provider.GetRequiredService<IClock>(),
            Options.Create(options),
            NullLogger<OrderReservationReconciliationService>.Instance);

    [Fact]
    public async Task 発注済み確定でOrderExecutedが発行される()
    {
        var reservations = new InMemoryOrderReservationStore();
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);
        await using var provider = BuildProvider(new StubProbe(ReservationProbeResult.Placed(Placed("BRK-7"))), reservations);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var service = BuildService(provider, new ReconciliationOptions { Enabled = true });

        var result = await service.ReconcileOnceAsync(CancellationToken.None);

        result.Terminalized.Should().Be(1);
        (await harness.Published.Any<OrderExecuted>(x => x.Context.Message.DecisionId == decisionId)).Should().BeTrue();
        reservations.Find(decisionId)!.State.Should().Be(OrderDispatchState.Completed);
    }

    [Fact]
    public async Task 不確定では何も発行されず据え置かれる()
    {
        var reservations = new InMemoryOrderReservationStore();
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);
        await using var provider = BuildProvider(new IndeterminateReservationBrokerProbe(), reservations);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var service = BuildService(provider, new ReconciliationOptions { Enabled = true });

        var result = await service.ReconcileOnceAsync(CancellationToken.None);

        result.Indeterminate.Should().Be(1);
        (await harness.Published.Any<OrderExecuted>()).Should().BeFalse();
        reservations.Find(decisionId)!.State.Should().Be(OrderDispatchState.Reserved);
    }

    [Fact]
    public async Task 無効時はExecuteAsyncが走査せず即座に戻る()
    {
        // fail-safe 既定: Enabled=false では滞留があっても一切触れない。
        var reservations = new InMemoryOrderReservationStore();
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);
        await using var provider = BuildProvider(new StubProbe(ReservationProbeResult.NotPlaced), reservations);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var service = BuildService(provider, new ReconciliationOptions { Enabled = false });

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        reservations.Find(decisionId)!.State.Should().Be(OrderDispatchState.Reserved, "無効時は解放しない");
        (await harness.Published.Any<OrderExecuted>()).Should().BeFalse();
    }
}
