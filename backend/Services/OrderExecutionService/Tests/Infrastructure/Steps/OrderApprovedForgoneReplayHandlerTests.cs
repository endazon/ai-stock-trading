using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.RecordTradeExpenses;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Infrastructure.Steps;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// 🔴 T-10-830, FR-05, FR-10, #876, IADR-0398: **本番と同じ Wolverine の配線**（ハンドラ・キュー・再試行・発行）で、
// 見送った承認の再配送を抑止したときに**何も発行せず・例外にもならない**ことを固定する。
// ハンドラは「見送りでなければ発注結果がある」と読んでいた（`result.Executed!`）ため、3 つ目の形（抑止）を
// 先に返さないと NullReferenceException で共通再試行へ落ちる。
public class OrderApprovedForgoneReplayHandlerTests
{
    private const string ServiceName = "ai-stock-trading.order-execution-service";

    // 接続の可否を途中で切り替えられるブローカー（OpenD の再起動→復帰）。
    private sealed class SwitchableBroker : IBrokerAdapter, IProtectiveOrderBroker
    {
        public bool Available { get; set; }

        public int PlaceCount { get; private set; }

        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) => Place(intent);

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            Place(closeIntent);

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) => Place(closeIntent);

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;

        private Task<BrokerOrder> Place(OrderIntent intent)
        {
            if (!Available)
                throw new BrokerUnavailableException("OpenD へ接続できません（テスト）");

            PlaceCount++;
            return Task.FromResult(new BrokerOrder(
                "ORD-" + PlaceCount, intent, OrderStatus.Filled, intent.Quantity, intent.Price,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
    }

    [Fact]
    public async Task 見送った承認の再配送は本番配線でも何も発行せず例外にもならない()
    {
        var broker = new SwitchableBroker { Available = false };
        var reservations = new InMemoryOrderReservationStore();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton<IBrokerAdapter>(broker);
                opts.Services.AddSingleton<IExecutedOrderStore>(new InMemoryExecutedOrderStore());
                opts.Services.AddSingleton<IOrderReservationStore>(reservations);
                opts.Services.AddSingleton<BusinessMetrics>();
                opts.Services.AddSingleton<AppSvc>();
                opts.Services.AddSingleton<IOrderExpenseSource, UnsuppliedOrderExpenseSource>();
                opts.Services.AddSingleton<TradeExpenseRecordingService>();
                opts.UseAiStockTradingRabbitMq(
                    ServiceName, "amqp://guest:guest@localhost:5672", typeof(OrderApprovedHandler).Assembly);
                opts.StubAllExternalTransports();
            })
            .StartAsync();

        var intent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
            100, 100m, PositionEffect.Close);
        var approved = new OrderApproved(Guid.NewGuid(), intent, 100, DateTimeOffset.UtcNow);

        var first = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(approved);
        first.Sent.MessagesOf<OrderDispatchForgone>().Should().ContainSingle("前提: 初回は見送りを 1 件発行する");

        broker.Available = true; // OpenD が復帰した
        var replay = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(approved); // 重複配送

        broker.PlaceCount.Should().Be(0, "見送った承認は再配送されても発注しない");
        replay.Sent.MessagesOf<OrderExecuted>().Should().BeEmpty("発注していない");
        replay.Sent.MessagesOf<OrderDispatchForgone>().Should().BeEmpty("理由を記録していないので再発行しない");
        replay.Executed.MessagesOf<OrderApproved>().Should().ContainSingle("例外にならず 1 回で処理を終える");
        reservations.Find(approved.DecisionId)!.State.Should().Be(OrderDispatchState.Forgone);

        await host.StopAsync();
    }
}
