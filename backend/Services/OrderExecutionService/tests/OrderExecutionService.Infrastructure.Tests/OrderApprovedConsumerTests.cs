using AiStockTrading.OrderExecution.Application.Adapters;
using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Infrastructure.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using AppSvc = AiStockTrading.OrderExecution.Application.Services.OrderExecutionService;

namespace AiStockTrading.OrderExecution.Infrastructure.Tests;

// FR-05, UC-01, UC-02: OrderApproved 購読 → ペーパー発注 → OrderExecuted 発行の検証。
//
// ADR-0013, IADR-0129, #354: MassTransit のテストハーネス（AddMassTransitTestHarness + harness.Consumed/Published）
// から Wolverine.Tracking（TrackActivity + session.Executed/Sent）へ移行した。表明の意味は同じ。
// 本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す。
public class OrderApprovedConsumerTests
{
    // 発注回数を数えるペーパーブローカ（再配送で二重発注しないことの確認用・#131）。
    private sealed class CountingPaperBroker : IBrokerAdapter
    {
        private readonly PaperBrokerAdapter _inner = new();

        // #386, IADR-0149: 内蔵 paper をそのまま名乗る（Stage 1 には算入されない側）。
        public BrokerProvider Provider => BrokerProvider.InternalPaper;

        public int PlaceCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            PlaceCount++;
            return _inner.PlaceOrderAsync(intent, ct);
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            _inner.GetOrderAsync(orderId, ct);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) =>
            _inner.CancelOrderAsync(orderId, ct);
    }

    private const string ServiceName = "ai-stock-trading.order-execution-service";

    private static Task<IHost> NewHostAsync(IExecutedOrderStore store, IBrokerAdapter broker) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton(broker);
                opts.Services.AddSingleton(store);
                // #131, IADR-0057: 発注前 DecisionId 予約（二重発注の防止）。
                opts.Services.AddSingleton<IOrderReservationStore, InMemoryOrderReservationStore>();
                opts.Services.AddSingleton<AppSvc>();

                opts.UseAiStockTradingRabbitMq(
                    ServiceName, "amqp://guest:guest@localhost:5672",
                    typeof(OrderApprovedHandler).Assembly);
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static OrderIntent NewIntent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 1_000m);

    [Fact]
    public async Task 承認注文を購読しOrderExecutedを発行する()
    {
        var store = new InMemoryExecutedOrderStore();
        using var host = await NewHostAsync(store, new PaperBrokerAdapter());

        var intent = NewIntent();
        var decisionId = Guid.NewGuid();
        var session = await host.TrackActivity()
            .InvokeMessageAndWaitAsync(new OrderApproved(decisionId, intent, 10, DateTimeOffset.UtcNow));

        session.Executed.MessagesOf<OrderApproved>().Should().NotBeEmpty();
        session.Sent.MessagesOf<OrderExecuted>().Should().NotBeEmpty();
        var executed = session.Sent.MessagesOf<OrderExecuted>().First();
        executed.DecisionId.Should().Be(decisionId);
        executed.Status.Should().Be(OrderStatus.Filled);
        store.GetAll().Should().ContainSingle(r => r.DecisionId == decisionId);

        await host.StopAsync();
    }

    [Fact]
    public async Task 同一OrderApprovedが再配送されても二重発注しない()
    {
        // #131: メッセージングの再配送（2s/10s/30s の再試行）で同じ OrderApproved が再処理されても、
        // ブローカ発注・台帳計上は高々1回に限定される（バス経由の end-to-end で固定する）。
        var store = new InMemoryExecutedOrderStore();
        var broker = new CountingPaperBroker();
        using var host = await NewHostAsync(store, broker);

        var approved = new OrderApproved(Guid.NewGuid(), NewIntent(), 10, DateTimeOffset.UtcNow);
        var first = await host.TrackActivity().InvokeMessageAndWaitAsync(approved);
        first.Executed.MessagesOf<OrderApproved>().Should().NotBeEmpty();
        var second = await host.TrackActivity().InvokeMessageAndWaitAsync(approved); // 再配送

        first.Executed.MessagesOf<OrderApproved>().Concat(second.Executed.MessagesOf<OrderApproved>())
            .Should().HaveCount(2, "同じメッセージが2回処理されること");
        broker.PlaceCount.Should().Be(1);        // 二重発注しない
        store.GetAll().Should().ContainSingle(); // 二重計上しない

        await host.StopAsync();
    }
}
