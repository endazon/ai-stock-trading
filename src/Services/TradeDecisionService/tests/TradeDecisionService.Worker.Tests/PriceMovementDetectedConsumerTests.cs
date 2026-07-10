using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.TradeDecision.Application.Adapters;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Worker.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AppSvc = AiStockTrading.TradeDecision.Application.Services.TradeDecisionService;

namespace AiStockTrading.TradeDecision.Worker.Tests;

// FR-04, UC-02: PriceMovementDetected 購読 → 判断成立時のみ TradeDecisionMade を発行することを検証する。
public class PriceMovementDetectedConsumerTests
{
    private sealed class FakeLlm(string output) : ILlmCompletionClient
    {
        public Task<string> CompleteAsync(string prompt, CancellationToken ct = default) => Task.FromResult(output);
    }
    private sealed class FakePolicy(DailyPolicy? p) : IDailyPolicyProvider { public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(p); }
    private sealed class FakeSizing : ISizingContextProvider
    {
        public SizingContext GetContext() => new(100_000m, 100_000m, 100_000m, 0, 0m,
            TradeMode.Paper, TradingDefaults.CreateRiskLimits());
    }

    private const string BuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30}""";

    // 判断経路の検証のため既定は常に開場のカレンダー（実時刻だと週末で揺れるため）。休場ケースは open:false を注入する。
    private sealed class CalendarStub(bool open) : IMarketCalendar
    {
        public bool IsOpen(Market market, DateTimeOffset instant) => open;
    }

    private static ServiceProvider Build(string llmOutput, DailyPolicy? policy, bool open = true)
    {
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock, SystemClock>()
            .AddSingleton<IMarketCalendar>(new CalendarStub(open))
            .AddSingleton<ILlmCompletionClient>(new FakeLlm(llmOutput))
            .AddSingleton<IDailyPolicyProvider>(new FakePolicy(policy))
            .AddSingleton<ISizingContextProvider, FakeSizing>()
            .AddScoped<AppSvc>()
            .AddMassTransitTestHarness(x => x.AddConsumer<PriceMovementDetectedConsumer>())
            .BuildServiceProvider(true);
    }

    private static PriceMovementDetected Trigger() =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, 1_040m, 1_000m, 0.04m, DateTimeOffset.UtcNow);

    [Fact]
    public async Task 方針ありでBuy判断ならTradeDecisionMadeを発行する()
    {
        await using var provider = Build(BuyJson, new DailyPolicy(new DateOnly(2026, 7, 10), "押し目買い"));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(Trigger());

        (await harness.Consumed.Any<PriceMovementDetected>()).Should().BeTrue();
        (await harness.Published.Any<TradeDecisionMade>()).Should().BeTrue();
        var decision = harness.Published.Select<TradeDecisionMade>().First().Context.Message;
        decision.Intent.PositionEffect.Should().Be(PositionEffect.Open);

        await harness.Stop();
    }

    [Fact]
    public async Task 休場日は判断せず発行しない_祝日ガード()
    {
        // FR-02, IADR-0023: イベント駆動系統も市場カレンダー閉場ならスキップする。
        await using var provider = Build(BuyJson, new DailyPolicy(new DateOnly(2026, 7, 10), "押し目買い"), open: false);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(Trigger());

        (await harness.Consumed.Any<PriceMovementDetected>()).Should().BeTrue();
        (await harness.Published.Any<TradeDecisionMade>()).Should().BeFalse();

        await harness.Stop();
    }

    [Fact]
    public async Task 確定済み日報が無ければ発行しない()
    {
        // FR-07: 方針なし → 取引しない。
        await using var provider = Build(BuyJson, policy: null);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(Trigger());

        (await harness.Consumed.Any<PriceMovementDetected>()).Should().BeTrue();
        (await harness.Published.Any<TradeDecisionMade>()).Should().BeFalse();

        await harness.Stop();
    }
}
