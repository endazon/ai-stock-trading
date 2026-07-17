using AiStockTrading.OrderExecution.Application.Adapters;
using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Application.Services;
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

// #154, FR-05, FR-19, IADR-0067: 訂正・取消の発行を MassTransit テストハーネスで検証する。
// 本 PR では駆動元（#141/#152・時限取消）を実装しないため、ここが配管の終端（発行）の担保になる。
public class OrderAmendmentDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 6, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private static OrderIntent Intent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, 10, 3000m);

    // 非終端の注文を扱うため immediateFill=false のペーパーブローカで組む（IADR-0067）。
    private static ServiceProvider BuildProvider(PaperBrokerAdapter broker, InMemoryExecutedOrderStore executedOrders) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock, FakeClock>()
            .AddSingleton<IBrokerAdapter>(broker)
            .AddSingleton<IOrderAmendmentBroker>(broker)
            .AddSingleton<IExecutedOrderStore>(executedOrders)
            .AddSingleton<IOrderLifecycleStore, InMemoryOrderLifecycleStore>()
            // IPublishEndpoint が scoped のため、本番配線（Program.cs）と同じく scoped で登録する。
            .AddScoped<OrderAmendmentService>()
            .AddScoped<OrderAmendmentDispatcher>()
            .AddMassTransitTestHarness()
            .BuildServiceProvider(true);

    private static async Task<Guid> PlaceAsync(PaperBrokerAdapter broker, InMemoryExecutedOrderStore store)
    {
        var decisionId = Guid.NewGuid();
        var execution = new AppSvc(broker, store, new InMemoryOrderReservationStore(), new FakeClock());
        await execution.ExecuteAsync(new OrderApproved(decisionId, Intent(), 10, Now));
        return decisionId;
    }

    [Fact]
    public async Task 取消すると_OrderCancelled_が発行される()
    {
        var broker = new PaperBrokerAdapter(immediateFill: false);
        var executedOrders = new InMemoryExecutedOrderStore();
        await using var provider = BuildProvider(broker, executedOrders);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var decisionId = await PlaceAsync(broker, executedOrders);

        using var scope = provider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<OrderAmendmentDispatcher>();
        await dispatcher.CancelAsync(decisionId, reason: "pause による強制取消");

        (await harness.Published.Any<OrderCancelled>()).Should().BeTrue();
        var published = harness.Published.Select<OrderCancelled>().Single().Context.Message;
        published.DecisionId.Should().Be(decisionId);
        published.Reason.Should().Be("pause による強制取消");
        published.CancelledAt.Should().Be(Now);
    }

    [Fact]
    public async Task 訂正すると_OrderModified_が訂正前後の値つきで発行される()
    {
        var broker = new PaperBrokerAdapter(immediateFill: false);
        var executedOrders = new InMemoryExecutedOrderStore();
        await using var provider = BuildProvider(broker, executedOrders);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var decisionId = await PlaceAsync(broker, executedOrders);

        using var scope = provider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<OrderAmendmentDispatcher>();
        await dispatcher.ModifyAsync(decisionId, quantity: 4, price: 2950m, reason: "数量縮小");

        (await harness.Published.Any<OrderModified>()).Should().BeTrue();
        var published = harness.Published.Select<OrderModified>().Single().Context.Message;
        published.DecisionId.Should().Be(decisionId);
        published.PreviousQuantity.Should().Be(10);
        published.PreviousPrice.Should().Be(3000m);
        published.Quantity.Should().Be(4);
        published.Price.Should().Be(2950m);
    }

    [Fact]
    public async Task 適用に失敗したら発行しない()
    {
        // 不整合（未適用なのに取消イベントだけが流れる）を作らない。
        var broker = new PaperBrokerAdapter(immediateFill: false);
        var executedOrders = new InMemoryExecutedOrderStore();
        await using var provider = BuildProvider(broker, executedOrders);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        using var scope = provider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<OrderAmendmentDispatcher>();
        var act = () => dispatcher.CancelAsync(Guid.NewGuid(), reason: "未知の判断ID");

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await harness.Published.Any<OrderCancelled>()).Should().BeFalse();
    }
}
