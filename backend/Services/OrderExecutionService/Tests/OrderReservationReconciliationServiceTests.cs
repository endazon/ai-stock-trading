using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Hosted;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace OrderExecutionService.Tests;

// #141, FR-05, IADR-0074: 自動リコンサイルの定期実行。既定無効・終端化した予約の OrderExecuted 発行・
// fail-safe（不確定は発行しない）を Wolverine のテストハーネス（Wolverine.Tracking）で検証する
// （ADR-0013 / IADR-0129 / #354。harness.Published → session.Sent。表明の意味は同じ）。
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
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 100m),
            OrderStatus.Filled, FilledQuantity: 10, AveragePrice: 100m, PlacedAt: StalledAt, CompletedAt: StalledAt);

    private const string ServiceName = "ai-stock-trading.order-execution-service";

    // 本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す。
    private static Task<IHost> BuildHostAsync(
        IReservationBrokerProbe probe, InMemoryOrderReservationStore reservations) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, FakeClock>();
                opts.Services.AddSingleton<IOrderReservationStore>(reservations);
                opts.Services.AddSingleton<IExecutedOrderStore, InMemoryExecutedOrderStore>();
                opts.Services.AddSingleton(probe);
                // FR-20, #386, IADR-0149 決定1: リコンサイラが再発行する OrderExecuted には
                // 実際に発注したアダプタの発注先が載る。本テストの構成は paper（実ブローカへ接続しない）。
                opts.Services.AddSingleton<IBrokerAdapter>(new PaperBrokerAdapter());
                opts.Services.AddScoped<OrderReservationReconciler>();

                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static OrderReservationReconciliationService BuildService(
        IHost host, ReconciliationOptions options) =>
        new(host.Services.GetRequiredService<IServiceScopeFactory>(),
            // 常駐（singleton）であり、Wolverine の IMessageBus（scoped）は注入できない。
            host.Services.GetRequiredService<IWolverineRuntime>(),
            host.Services.GetRequiredService<IClock>(),
            Options.Create(options),
            NullLogger<OrderReservationReconciliationService>.Instance);

    [Fact]
    public async Task 発注済み確定でOrderExecutedが発行される()
    {
        var reservations = new InMemoryOrderReservationStore();
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);
        using var host = await BuildHostAsync(new StubProbe(ReservationProbeResult.Placed(Placed("BRK-7"))), reservations);
        var service = BuildService(host, new ReconciliationOptions { Enabled = true });

        ReservationReconciliationResult result = null!;
        Func<IMessageContext, Task> reconcile = async _ =>
            result = await service.ReconcileOnceAsync(CancellationToken.None);
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(reconcile);

        result.Terminalized.Should().Be(1);
        session.Sent.MessagesOf<OrderExecuted>().Should().Contain(m => m.DecisionId == decisionId);
        reservations.Find(decisionId)!.State.Should().Be(OrderDispatchState.Completed);

        await host.StopAsync();
    }

    [Fact]
    public async Task 不確定では何も発行されず据え置かれる()
    {
        var reservations = new InMemoryOrderReservationStore();
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);
        using var host = await BuildHostAsync(new IndeterminateReservationBrokerProbe(), reservations);
        var service = BuildService(host, new ReconciliationOptions { Enabled = true });

        ReservationReconciliationResult result = null!;
        Func<IMessageContext, Task> reconcile = async _ =>
            result = await service.ReconcileOnceAsync(CancellationToken.None);
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(reconcile);

        result.Indeterminate.Should().Be(1);
        session.Sent.MessagesOf<OrderExecuted>().Should().BeEmpty();
        reservations.Find(decisionId)!.State.Should().Be(OrderDispatchState.Reserved);

        await host.StopAsync();
    }

    [Fact]
    public async Task 無効時はExecuteAsyncが走査せず即座に戻る()
    {
        // fail-safe 既定: Enabled=false では滞留があっても一切触れない。
        var reservations = new InMemoryOrderReservationStore();
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);
        using var host = await BuildHostAsync(new StubProbe(ReservationProbeResult.NotPlaced), reservations);
        var service = BuildService(host, new ReconciliationOptions { Enabled = false });

        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(_ => StartAndStopAsync(service));

        reservations.Find(decisionId)!.State.Should().Be(OrderDispatchState.Reserved, "無効時は解放しない");
        session.Sent.MessagesOf<OrderExecuted>().Should().BeEmpty();

        await host.StopAsync();
    }

    // 常駐を起動して止めるだけの補助（元テストと呼び出し順は同じ）。
    private static async Task StartAndStopAsync(OrderReservationReconciliationService service)
    {
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }
}
