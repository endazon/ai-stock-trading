using AiStockTrading.Audit.Application.Adapters;
using AiStockTrading.Audit.Application.Ports;
using AiStockTrading.Audit.Worker.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.Audit.Worker.Tests;

// FR-11, UC-07, IADR-0019: 全ドメインイベントを購読して監査台帳へ記録する Consumer を
// MassTransit テストハーネス + インメモリ台帳で検証する。
public class AuditEventConsumersTests
{
    private static OrderIntent Intent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, 10, 1_000m);

    private static ServiceProvider BuildProvider(InMemoryAuditEventStore store) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock, SystemClock>()
            .AddSingleton<IAuditEventStore>(store)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<PriceMovementDetectedAuditConsumer>();
                x.AddConsumer<StopLossTriggeredAuditConsumer>();
                x.AddConsumer<TradeDecisionMadeAuditConsumer>();
                x.AddConsumer<OrderApprovedAuditConsumer>();
                x.AddConsumer<OrderRejectedAuditConsumer>();
                x.AddConsumer<OrderExecutedAuditConsumer>();
            })
            .BuildServiceProvider(true);

    [Fact]
    public async Task 注文チェーンのイベントは同一_DecisionId_相関で記録される()
    {
        var store = new InMemoryAuditEventStore();
        await using var provider = BuildProvider(store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var decisionId = Guid.NewGuid();
        await harness.Bus.Publish(new TradeDecisionMade(decisionId, Intent(), "買い", DateTimeOffset.UtcNow));
        await harness.Bus.Publish(new OrderApproved(decisionId, Intent(), 10, DateTimeOffset.UtcNow));
        await harness.Bus.Publish(new OrderExecuted(decisionId, "ORD-1", OrderStatus.Filled, 10, 1_050m, DateTimeOffset.UtcNow));

        (await harness.Consumed.Any<TradeDecisionMade>()).Should().BeTrue();
        (await harness.Consumed.Any<OrderApproved>()).Should().BeTrue();
        (await harness.Consumed.Any<OrderExecuted>()).Should().BeTrue();

        var trail = store.GetByCorrelation(decisionId);
        trail.Select(e => e.EventType).Should()
            .Contain(new[] { "TradeDecisionMade", "OrderApproved", "OrderExecuted" });

        await harness.Stop();
    }

    [Fact]
    public async Task 拒否イベントは理由つきで記録される()
    {
        var store = new InMemoryAuditEventStore();
        await using var provider = BuildProvider(store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var decisionId = Guid.NewGuid();
        var reasons = new[] { RejectionReason.KillSwitchActive };
        await harness.Bus.Publish(new OrderRejected(decisionId, Intent(), reasons, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<OrderRejected>()).Should().BeTrue();

        var trail = store.GetByCorrelation(decisionId);
        trail.Should().ContainSingle(e => e.EventType == "OrderRejected")
            .Which.Summary.Should().Contain(nameof(RejectionReason.KillSwitchActive));

        await harness.Stop();
    }

    [Fact]
    public async Task 市場イベントは_EventId_相関で記録される()
    {
        var store = new InMemoryAuditEventStore();
        await using var provider = BuildProvider(store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var eventId = Guid.NewGuid();
        await harness.Bus.Publish(new StopLossTriggered(eventId, "7203", Market.Japan, TradeSide.Buy, 5, 950m, 940m, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<StopLossTriggered>()).Should().BeTrue();

        store.GetByCorrelation(eventId).Should().ContainSingle(e => e.EventType == "StopLossTriggered");

        await harness.Stop();
    }
}
