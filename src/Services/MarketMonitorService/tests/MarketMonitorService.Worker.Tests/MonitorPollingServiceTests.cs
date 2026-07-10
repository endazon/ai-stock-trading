using AiStockTrading.MarketMonitor.Application.Adapters;
using AiStockTrading.MarketMonitor.Application.Ports;
using AiStockTrading.MarketMonitor.Domain;
using AiStockTrading.MarketMonitor.Worker.Composable.Polling;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using AppSvc = AiStockTrading.MarketMonitor.Application.Services.MarketMonitorService;

namespace AiStockTrading.MarketMonitor.Worker.Tests;

// FR-03, UC-02, ADR-0003: ポーリング巡回（RunOnceAsync）の検証。市場開場時に評価結果を発行し、閉場時は発行しない。
public class MonitorPollingServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 1, 0, 0, TimeSpan.Zero);
    private static readonly MonitoredSymbol Aapl = new("AAPL", Market.UnitedStates);

    private sealed class Harness : IAsyncDisposable
    {
        public FakeClock Clock { get; } = new(Now);
        public FakeSchedule Schedule { get; } = new(open: true);
        public FakeMarketDataSource Market { get; } = new();
        public InMemoryMonitoredSymbolStore Settings { get; }
        public InMemoryPositionStore Positions { get; } = new();
        public InMemoryPriceBaselineStore Baselines { get; } = new();
        public InMemoryCooldownStore Cooldowns { get; } = new();
        private ServiceProvider? _provider;

        public Harness(MarketMonitorSettings settings) => Settings = new InMemoryMonitoredSymbolStore(settings);

        public async Task<(MonitorPollingService Service, ITestHarness Harness)> StartAsync()
        {
            _provider = new ServiceCollection()
                .AddSingleton<IMonitoredSymbolStore>(Settings)
                .AddSingleton<IPositionStore>(Positions)
                .AddSingleton<IPriceBaselineStore>(Baselines)
                .AddSingleton<ICooldownStore>(Cooldowns)
                .AddSingleton<IMarketDataSource>(Market)
                .AddSingleton<IClock>(Clock)
                .AddScoped<AppSvc>()
                .AddMassTransitTestHarness()
                .BuildServiceProvider(true);

            var testHarness = _provider.GetRequiredService<ITestHarness>();
            await testHarness.Start();

            var service = new MonitorPollingService(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Schedule, Clock, Options.Create(new MonitorOptions()),
                NullLogger<MonitorPollingService>.Instance);

            return (service, testHarness);
        }

        public async ValueTask DisposeAsync()
        {
            if (_provider is not null) await _provider.DisposeAsync();
        }
    }

    private static MarketMonitorSettings Settings(params MonitoredSymbol[] symbols) => new()
    {
        MovementThresholdRatio = 0.03m,
        Cooldown = TimeSpan.FromMinutes(15),
        MonitoredSymbols = symbols,
    };

    [Fact]
    public async Task 市場開場時に閾値超過なら価格変動イベントを発行する()
    {
        await using var h = new Harness(Settings(Aapl));
        h.Baselines.SetBaseline("AAPL", Market.UnitedStates, 1_000m);
        h.Market.Set("AAPL", Market.UnitedStates, 1_040m); // +4%
        var (service, harness) = await h.StartAsync();

        await service.RunOnceAsync(CancellationToken.None);

        (await harness.Published.Any<PriceMovementDetected>()).Should().BeTrue();
    }

    [Fact]
    public async Task 市場閉場中はイベントを発行しない()
    {
        await using var h = new Harness(Settings(Aapl));
        h.Schedule.Open = false;
        h.Baselines.SetBaseline("AAPL", Market.UnitedStates, 1_000m);
        h.Market.Set("AAPL", Market.UnitedStates, 1_040m);
        var (service, harness) = await h.StartAsync();

        await service.RunOnceAsync(CancellationToken.None);

        (await harness.Published.Any<PriceMovementDetected>()).Should().BeFalse();
    }

    [Fact]
    public async Task 損切りライン到達時に損切りイベントを発行する()
    {
        await using var h = new Harness(Settings()); // 監視銘柄なし・保有のみ
        h.Positions.Set([new HeldPosition("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 970m)]);
        h.Market.Set("AAPL", Market.UnitedStates, 960m);
        var (service, harness) = await h.StartAsync();

        await service.RunOnceAsync(CancellationToken.None);

        (await harness.Published.Any<StopLossTriggered>()).Should().BeTrue();
    }
}
