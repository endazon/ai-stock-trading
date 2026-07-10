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

// FR-02, UC-01, IADR-0023: 定時系統の合流。InformationCollected 購読 → watchlist 巡回 → 開場銘柄のみ判断 → TradeDecisionMade 発行。
public class InformationCollectedConsumerTests
{
    private const string BuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30}""";

    private sealed class FakeLlm(string output) : ILlmCompletionClient
    {
        public Task<string> CompleteAsync(string prompt, CancellationToken ct = default) => Task.FromResult(output);
    }
    private sealed class FakePolicy : IDailyPolicyProvider
    {
        public DailyPolicy? GetCurrent() => new(new DateOnly(2026, 7, 10), "押し目買い");
    }
    private sealed class FakeSizing : ISizingContextProvider
    {
        public SizingContext GetContext() => new(100_000m, 100_000m, 100_000m, 0, 0m,
            TradeMode.Paper, TradingDefaults.CreateRiskLimits());
    }
    private sealed class FakeWatchlist(params WatchedSymbol[] symbols) : IWatchlistProvider
    {
        public IReadOnlyList<WatchedSymbol> GetWatchlist() => symbols;
    }
    private sealed class CalendarStub(bool open) : IMarketCalendar
    {
        public bool IsOpen(Market market, DateTimeOffset instant) => open;
    }

    private static ServiceProvider Build(IWatchlistProvider watchlist, IMarketCalendar calendar) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock, SystemClock>()
            .AddSingleton(calendar)
            .AddSingleton(watchlist)
            .AddSingleton<ILlmCompletionClient>(new FakeLlm(BuyJson))
            .AddSingleton<IDailyPolicyProvider, FakePolicy>()
            .AddSingleton<ISizingContextProvider, FakeSizing>()
            .AddScoped<AppSvc>()
            .AddMassTransitTestHarness(x => x.AddConsumer<InformationCollectedConsumer>())
            .BuildServiceProvider(true);

    [Fact]
    public async Task 開場中は監視銘柄について判断し_TradeDecisionMade_を発行する()
    {
        await using var provider = Build(
            new FakeWatchlist(new WatchedSymbol("AAPL", Market.UnitedStates)),
            new CalendarStub(open: true));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new InformationCollected(Guid.NewGuid(), 3, DateTimeOffset.UtcNow));

        (await harness.Consumed.Any<InformationCollected>()).Should().BeTrue();
        (await harness.Published.Any<TradeDecisionMade>()).Should().BeTrue();
        var decision = harness.Published.Select<TradeDecisionMade>().First().Context.Message;
        decision.Intent.Symbol.Should().Be("AAPL");

        await harness.Stop();
    }

    [Fact]
    public async Task 休場日は判断せず発行しない()
    {
        await using var provider = Build(
            new FakeWatchlist(new WatchedSymbol("AAPL", Market.UnitedStates)),
            new CalendarStub(open: false));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new InformationCollected(Guid.NewGuid(), 3, DateTimeOffset.UtcNow));

        (await harness.Consumed.Any<InformationCollected>()).Should().BeTrue();
        (await harness.Published.Any<TradeDecisionMade>()).Should().BeFalse();

        await harness.Stop();
    }

    [Fact]
    public async Task 監視銘柄が空なら発行しない()
    {
        await using var provider = Build(new FakeWatchlist(), new CalendarStub(open: true));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new InformationCollected(Guid.NewGuid(), 3, DateTimeOffset.UtcNow));

        (await harness.Consumed.Any<InformationCollected>()).Should().BeTrue();
        (await harness.Published.Any<TradeDecisionMade>()).Should().BeFalse();

        await harness.Stop();
    }
}
