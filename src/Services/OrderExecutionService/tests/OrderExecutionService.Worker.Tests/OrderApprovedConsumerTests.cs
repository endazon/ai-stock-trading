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
    [Fact]
    public async Task 承認注文を購読しOrderExecutedを発行する()
    {
        var store = new InMemoryExecutedOrderStore();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock, SystemClock>()
            .AddSingleton<IBrokerAdapter>(new PaperBrokerAdapter())
            .AddSingleton<IExecutedOrderStore>(store)
            .AddSingleton<AppSvc>()
            .AddMassTransitTestHarness(x => x.AddConsumer<OrderApprovedConsumer>())
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var intent = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, TradeMode.Paper, 10, 1_000m);
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
}
