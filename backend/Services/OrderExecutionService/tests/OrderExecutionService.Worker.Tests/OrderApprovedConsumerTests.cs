using AiStockTrading.OrderExecution.Application.Adapters;
using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Worker.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AppSvc = AiStockTrading.OrderExecution.Application.Services.OrderExecutionService;

namespace AiStockTrading.OrderExecution.Worker.Tests;

// FR-05, UC-01, UC-02: OrderApproved 購読 → ペーパー発注 → OrderExecuted 発行の検証。
public class OrderApprovedConsumerTests
{
    // 発注回数を数えるペーパーブローカ（再配送で二重発注しないことの確認用・#131）。
    private sealed class CountingPaperBroker : IBrokerAdapter
    {
        private readonly PaperBrokerAdapter _inner = new();

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

    private static ServiceProvider NewProvider(IExecutedOrderStore store, IBrokerAdapter broker) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock, SystemClock>()
            .AddSingleton(broker)
            .AddSingleton(store)
            // #131, IADR-0057: 発注前 DecisionId 予約（二重発注の防止）。
            .AddSingleton<IOrderReservationStore, InMemoryOrderReservationStore>()
            .AddSingleton<AppSvc>()
            .AddMassTransitTestHarness(x => x.AddConsumer<OrderApprovedConsumer>())
            .BuildServiceProvider(true);

    private static OrderIntent NewIntent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, 10, 1_000m);

    [Fact]
    public async Task 承認注文を購読しOrderExecutedを発行する()
    {
        var store = new InMemoryExecutedOrderStore();
        await using var provider = NewProvider(store, new PaperBrokerAdapter());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var intent = NewIntent();
        var decisionId = Guid.NewGuid();
        await harness.Bus.Publish(new OrderApproved(decisionId, intent, 10, DateTimeOffset.UtcNow));

        (await harness.Consumed.Any<OrderApproved>()).Should().BeTrue();
        (await harness.Published.Any<OrderExecuted>()).Should().BeTrue();
        var executed = harness.Published.Select<OrderExecuted>().First().Context.Message;
        executed.DecisionId.Should().Be(decisionId);
        executed.Status.Should().Be(OrderStatus.Filled);
        store.GetAll().Should().ContainSingle(r => r.DecisionId == decisionId);

        await harness.Stop();
    }

    [Fact]
    public async Task 同一OrderApprovedが再配送されても二重発注しない()
    {
        // #131: MassTransit の再配送（UseAiStockTradingRetry）で同じ OrderApproved が再処理されても、
        // ブローカ発注・台帳計上は高々1回に限定される（バス経由の end-to-end で固定する）。
        var store = new InMemoryExecutedOrderStore();
        var broker = new CountingPaperBroker();
        await using var provider = NewProvider(store, broker);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var approved = new OrderApproved(Guid.NewGuid(), NewIntent(), 10, DateTimeOffset.UtcNow);
        await harness.Bus.Publish(approved);
        (await harness.Consumed.Any<OrderApproved>()).Should().BeTrue();
        await harness.Bus.Publish(approved); // 再配送
        await harness.InactivityTask;

        harness.Consumed.Select<OrderApproved>().Should().HaveCount(2, "同じメッセージが2回処理されること");
        broker.PlaceCount.Should().Be(1);        // 二重発注しない
        store.GetAll().Should().ContainSingle(); // 二重計上しない

        await harness.Stop();
    }
}
