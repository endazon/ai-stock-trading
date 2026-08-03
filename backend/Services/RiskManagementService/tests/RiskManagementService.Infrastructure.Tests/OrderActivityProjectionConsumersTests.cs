using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain.Manipulation;
using AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.RiskManagement.Infrastructure.Tests;

// FR-19, #154, IADR-0067: 注文系イベントを購読して注文アクティビティへ射影する Consumer 群を
// MassTransit テストハーネス + インメモリ射影ストアで検証する。射影後に窓として読めることまで確かめる。
public class OrderActivityProjectionConsumersTests
{
    private static readonly DateTimeOffset Base = new(2026, 7, 18, 6, 0, 0, TimeSpan.Zero);

    private static OrderIntent Intent(int qty = 10) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, qty, 3_000m);

    private static ServiceProvider BuildProvider(InMemoryOrderActivityStore store) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IOrderActivityStore>(store)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<OrderApprovedActivityConsumer>();
                x.AddConsumer<OrderExecutedActivityConsumer>();
                x.AddConsumer<OrderModifiedActivityConsumer>();
                x.AddConsumer<OrderCancelledActivityConsumer>();
            })
            .BuildServiceProvider(true);

    private static OrderActivityWindow Window(InMemoryOrderActivityStore store) =>
        store.GetRecentActivity("AAPL", Market.UnitedStates, Base.AddMinutes(1), TimeSpan.FromMinutes(5));

    [Fact]
    public async Task 承認から取消までを購読し注文アクティビティへ射影する()
    {
        var store = new InMemoryOrderActivityStore();
        await using var provider = BuildProvider(store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var decisionId = Guid.NewGuid();
        await harness.Bus.Publish(new OrderApproved(decisionId, Intent(10), 10, Base));
        (await harness.Consumed.Any<OrderApproved>()).Should().BeTrue();
        await harness.Bus.Publish(new OrderModified(decisionId, "ORD-1", 10, 3_000m, 6, 2_950m, "縮小", Base.AddSeconds(10)));
        (await harness.Consumed.Any<OrderModified>()).Should().BeTrue();
        await harness.Bus.Publish(new OrderCancelled(decisionId, "ORD-1", "pause 強制取消", Base.AddSeconds(20)));
        (await harness.Consumed.Any<OrderCancelled>()).Should().BeTrue();

        var record = Window(store).Records.Should().ContainSingle().Subject;
        record.AmendmentCount.Should().Be(1);
        record.Quantity.Should().Be(6);
        record.Status.Should().Be(OrderStatus.Cancelled);
        record.IsCancelledWithoutFill.Should().BeTrue();

        await harness.Stop();
    }

    [Fact]
    public async Task 約定は状態と約定数を射影する()
    {
        var store = new InMemoryOrderActivityStore();
        await using var provider = BuildProvider(store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var decisionId = Guid.NewGuid();
        await harness.Bus.Publish(new OrderApproved(decisionId, Intent(10), 10, Base));
        (await harness.Consumed.Any<OrderApproved>()).Should().BeTrue();
        await harness.Bus.Publish(new OrderExecuted(decisionId, "ORD-1", OrderStatus.Filled, 10, 3_010m, Base.AddSeconds(1)));
        (await harness.Consumed.Any<OrderExecuted>()).Should().BeTrue();

        var record = Window(store).Records.Should().ContainSingle().Subject;
        record.Status.Should().Be(OrderStatus.Filled);
        record.FilledQuantity.Should().Be(10);
        record.IsFilledOrPartial.Should().BeTrue();

        await harness.Stop();
    }

    [Fact]
    public async Task 相関する承認が無い約定は射影されない()
    {
        var store = new InMemoryOrderActivityStore();
        await using var provider = BuildProvider(store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new OrderExecuted(Guid.NewGuid(), "ORD-1", OrderStatus.Filled, 10, 3_010m, Base));
        (await harness.Consumed.Any<OrderExecuted>()).Should().BeTrue();

        Window(store).Records.Should().BeEmpty();

        await harness.Stop();
    }
}
