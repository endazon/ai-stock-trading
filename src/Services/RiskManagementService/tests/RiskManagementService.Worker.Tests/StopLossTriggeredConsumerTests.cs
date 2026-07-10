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
using Xunit;

namespace AiStockTrading.RiskManagement.Worker.Tests;

// FR-10, FR-03, ADR-0003, IADR-0015: StopLossTriggered 購読 → LLM 迂回で Close の OrderApproved 発行を検証する。
public class StopLossTriggeredConsumerTests
{
    private static ServiceProvider BuildProvider(IKillSwitchStore? killSwitch = null)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock, SystemClock>()
            .AddSingleton<IRiskSettingsStore, InMemoryRiskSettingsStore>()
            .AddSingleton<StopLossExecutionService>();
        // kill switch は損切り執行に影響しない（無条件）ことを示すため、任意で起動状態を注入する。
        if (killSwitch is not null)
        {
            services.AddSingleton(killSwitch);
        }

        return services
            .AddMassTransitTestHarness(x => x.AddConsumer<StopLossTriggeredConsumer>())
            .BuildServiceProvider(true);
    }

    private static StopLossTriggered Triggered() =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 10, 950m, 970m, DateTimeOffset.UtcNow);

    [Fact]
    public async Task 損切りイベントで決済のOrderApprovedを発行する()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(Triggered());

        (await harness.Consumed.Any<StopLossTriggered>()).Should().BeTrue();
        (await harness.Published.Any<OrderApproved>()).Should().BeTrue();
        var approved = harness.Published.Select<OrderApproved>().First().Context.Message;
        approved.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        approved.Intent.Side.Should().Be(TradeSide.Sell);

        await harness.Stop();
    }

    [Fact]
    public async Task kill_switch_起動中でも損切りは無条件に発行される()
    {
        // ADR-0003: 損切りは kill switch 起動中でも必ず実行する。
        var killSwitch = new InMemoryKillSwitchStore();
        killSwitch.SetState(new KillSwitchState(true, "user", "緊急停止", DateTimeOffset.UtcNow));
        await using var provider = BuildProvider(killSwitch);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(Triggered());

        (await harness.Published.Any<OrderApproved>()).Should().BeTrue();

        await harness.Stop();
    }
}
