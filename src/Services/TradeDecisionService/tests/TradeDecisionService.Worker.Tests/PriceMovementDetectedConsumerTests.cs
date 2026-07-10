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
    private sealed class FakePolicy(DailyPolicy? p) : IDailyPolicyProvider { public DailyPolicy? GetCurrent() => p; }
    private sealed class FakeSizing : ISizingContextProvider
    {
        public SizingContext GetContext() => new(100_000m, 100_000m, 100_000m, 0, 0m,
            TradeMode.Paper, TradingDefaults.CreateRiskLimits());
    }

    private const string BuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30}""";

    private static ServiceProvider Build(string llmOutput, DailyPolicy? policy)
    {
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock, SystemClock>()
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
