using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
using OrderExecutionService.Features.OrderExecution.RecordTradeExpenses;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// FR-10, FR-12, UC-02, ADR-0040 決定1（S1）, #820, IADR-0344 決定4・決定9: 発注執行が StopLossTriggered を購読し、
// S1 の承認 → ソフトウェア逆指値の配置 → 損切りライン到達 → 成行決済 の発行までを本番と同じ配線（キュー名・発見範囲）で通す。
// 受け入れ基準 11（購読と発行）と、#820 の受け入れ基準「SIMULATE＋S1 で建玉が残り、到達で成行決済。二重決済が起きない」。
public class StopLossTriggeredConsumerTests
{
    private const string ServiceName = "ai-stock-trading.order-execution-service";

    // moomoo SIMULATE を名乗り、エントリーは即時約定・成行決済は受付で返す。建玉は約定と決済から計算する。
    private sealed class SimulateBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        private int _position;

        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public int StopPlaceCount { get; private set; }
        public int MarketCloseCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            _position += intent.Quantity;
            return Task.FromResult(new BrokerOrder(
                $"entry-{Guid.NewGuid():N}", intent, OrderStatus.Filled, intent.Quantity, intent.Price,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default)
        {
            StopPlaceCount++;
            return Task.FromResult(new BrokerOrder(
                "stop", closeIntent, OrderStatus.Rejected, 0, 0m, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCloseCount++;
            return Task.FromResult(new BrokerOrder(
                $"close-{MarketCloseCount}", closeIntent, OrderStatus.Accepted, 0, 0m, DateTimeOffset.UtcNow, null));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BrokerPositionSnapshot>?>(
                [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, _position, 1_000m)]);
    }

    private static Task<IHost> NewHostAsync(SimulateBroker broker, InMemoryProtectiveStopOrderStore stops) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton<IBrokerAdapter>(broker);
                opts.Services.AddSingleton<IBrokerPositionSource>(broker);
                opts.Services.AddSingleton<IExecutedOrderStore, InMemoryExecutedOrderStore>();
                opts.Services.AddSingleton<IOrderReservationStore, InMemoryOrderReservationStore>();
                opts.Services.AddSingleton<IProtectiveStopOrderStore>(stops);
                opts.Services.AddSingleton<BusinessMetrics>();
                opts.Services.AddSingleton(sp => new AppSvc(
                    sp.GetRequiredService<IBrokerAdapter>(), sp.GetRequiredService<IExecutedOrderStore>(),
                    sp.GetRequiredService<IOrderReservationStore>(), sp.GetRequiredService<IClock>(),
                    sp.GetRequiredService<IProtectiveStopOrderStore>()));
                opts.Services.AddSingleton<IOrderExpenseSource, UnsuppliedOrderExpenseSource>();
                opts.Services.AddSingleton<TradeExpenseRecordingService>();
                opts.Services.AddSingleton(sp => new SoftwareStopExecutor(
                    sp.GetRequiredService<IBrokerAdapter>(), sp.GetRequiredService<IBrokerPositionSource>(),
                    sp.GetRequiredService<IProtectiveStopOrderStore>(), sp.GetRequiredService<IExecutedOrderStore>(),
                    sp.GetRequiredService<IOrderReservationStore>(), sp.GetRequiredService<IClock>()));

                opts.UseAiStockTradingRabbitMq(
                    ServiceName, "amqp://guest:guest@localhost:5672",
                    typeof(StopLossTriggeredHandler).Assembly);
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    [Fact]
    public void 発注執行の損切り到達の購読キューはサービス名で分離される()
    {
        WolverineExtensions.QueueNameFor(ServiceName, typeof(StopLossTriggered))
            .Should().Be("ai-stock-trading.order-execution-service.StopLossTriggered",
                "リスク管理・監査・通知の同名購読とキューを共有すると取り合いになる（IADR-0129 決定1）");
    }

    [Fact]
    public async Task S1の建玉は損切りライン到達で一度だけ成行決済されSoftwareStopExecutedが発行される()
    {
        var broker = new SimulateBroker();
        var stops = new InMemoryProtectiveStopOrderStore();
        using var host = await NewHostAsync(broker, stops);

        var decisionId = Guid.NewGuid();
        var entry = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 10, 1_000m, PositionEffect.Open, StopLossPrice: 950m, FxRateToBase: 1m);
        var approvedSession = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderApproved(
            decisionId, entry, 10, DateTimeOffset.UtcNow.AddMinutes(-5), StopLossMethod: StopLossExecutionMethod.SoftwareStop));

        broker.StopPlaceCount.Should().Be(0, "S1 はブローカーへ逆指値を出さない");
        approvedSession.Sent.MessagesOf<SoftwareStopArmed>().Should().ContainSingle()
            .Which.EntryDecisionId.Should().Be(decisionId);
        approvedSession.Sent.MessagesOf<ProtectiveStopCoverageLost>().Should().BeEmpty("建玉を解消しない");

        var trigger = new StopLossTriggered(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 10, 940m, 950m, DateTimeOffset.UtcNow);
        var first = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(trigger);
        // 市場監視の次の巡回（価格は戻っていない）と、同じメッセージの再配送。
        var second = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            trigger with { EventId = Guid.NewGuid(), Price = 935m, DetectedAt = DateTimeOffset.UtcNow });
        var redelivered = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(trigger);

        first.Executed.MessagesOf<StopLossTriggered>().Should().NotBeEmpty();
        var executed = first.Sent.MessagesOf<SoftwareStopExecuted>().Should().ContainSingle().Which;
        executed.Outcome.Should().Be(SoftwareStopOutcome.ClosePlaced);
        executed.CloseIntent!.Quantity.Should().Be(10);
        executed.CloseDecisionId.Should().Be(ProtectiveStopIds.SoftwareCloseDecisionId(decisionId, 1));

        broker.MarketCloseCount.Should().Be(1, "二重決済しない");
        second.Sent.MessagesOf<SoftwareStopExecuted>().Should().BeEmpty();
        redelivered.Sent.MessagesOf<SoftwareStopExecuted>().Should().BeEmpty();
        stops.Find(decisionId)!.State.Should().Be(ProtectiveStopState.Completed);

        await host.StopAsync();
    }

    [Fact]
    public async Task S0やS2の建玉には損切りライン到達で何も発注しない()
    {
        var broker = new SimulateBroker();
        var stops = new InMemoryProtectiveStopOrderStore();
        using var host = await NewHostAsync(broker, stops);

        var entry = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 10, 1_000m, PositionEffect.Open, StopLossPrice: 950m, FxRateToBase: 1m);
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderApproved(
            Guid.NewGuid(), entry, 10, DateTimeOffset.UtcNow.AddMinutes(-5), StopLossMethod: StopLossExecutionMethod.NoProtectiveStop));

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new StopLossTriggered(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 10, 940m, 950m, DateTimeOffset.UtcNow));

        session.Executed.MessagesOf<StopLossTriggered>().Should().NotBeEmpty();
        broker.MarketCloseCount.Should().Be(0, "S2 は誰も決済しない（手法の記録を持つ S1 の行だけが対象）");
        session.Sent.MessagesOf<SoftwareStopExecuted>().Should().BeEmpty();

        await host.StopAsync();
    }
}
