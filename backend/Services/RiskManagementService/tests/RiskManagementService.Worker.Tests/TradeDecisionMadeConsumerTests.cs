using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Worker.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiStockTrading.RiskManagement.Worker.Tests;

// FR-10, UC-01, UC-02: TradeDecisionMade 購読 → 承認/拒否発行を MassTransit テストハーネスで検証する。
public class TradeDecisionMadeConsumerTests
{
    private static OrderIntent Entry() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, 10, 1_000m);

    private static ServiceProvider BuildProvider(IKillSwitchStore killSwitch)
    {
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock, SystemClock>()
            .AddSingleton<IBusinessCalendar, WeekendBusinessCalendar>()
            .AddSingleton<IRiskSettingsStore, InMemoryRiskSettingsStore>()
            .AddSingleton(killSwitch)
            .AddSingleton<ILockoutStore, InMemoryLockoutStore>()
            .AddSingleton<ISettingsChangeLog, InMemorySettingsChangeLog>()
            .AddSingleton<IPortfolioStateProvider>(new HealthyPortfolioProvider())
            .AddSingleton<PortfolioSnapshotBuilder>()
            .AddSingleton(sp => new OrderScreeningService(
                sp.GetRequiredService<IRiskSettingsStore>(),
                sp.GetRequiredService<PortfolioSnapshotBuilder>(),
                sp.GetRequiredService<ILockoutStore>(),
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<IBusinessCalendar>(),
                null))
            .AddMassTransitTestHarness(x => x.AddConsumer<TradeDecisionMadeConsumer>())
            .BuildServiceProvider(true);
    }

    [Fact]
    public async Task 承認された注文は_OrderApproved_を発行する()
    {
        await using var provider = BuildProvider(new InMemoryKillSwitchStore());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new TradeDecisionMade(Guid.NewGuid(), Entry(), "判断", DateTimeOffset.UtcNow));

        (await harness.Consumed.Any<TradeDecisionMade>()).Should().BeTrue();
        (await harness.Published.Any<OrderApproved>()).Should().BeTrue();
        (await harness.Published.Any<OrderRejected>()).Should().BeFalse();

        await harness.Stop();
    }

    [Fact]
    public async Task kill_switch_起動中は_OrderRejected_を発行する()
    {
        var killSwitch = new InMemoryKillSwitchStore();
        killSwitch.SetState(new KillSwitchState(true, "user", "停止", DateTimeOffset.UtcNow));
        await using var provider = BuildProvider(killSwitch);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new TradeDecisionMade(Guid.NewGuid(), Entry(), "判断", DateTimeOffset.UtcNow));

        (await harness.Published.Any<OrderRejected>()).Should().BeTrue();
        var rejected = harness.Published.Select<OrderRejected>().First().Context.Message;
        rejected.Reasons.Should().Contain(RejectionReason.KillSwitchActive);

        await harness.Stop();
    }

    // 全統制を通過する健全な運用状態（資金 10 万・損益ゼロ）。
    private sealed class HealthyPortfolioProvider : IPortfolioStateProvider
    {
        public PortfolioState GetCurrent() => new() { Capital = 100_000m };
    }
}
