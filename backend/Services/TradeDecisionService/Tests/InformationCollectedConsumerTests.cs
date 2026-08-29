using RiskManagementService.Domain;
using AiStockTrading.TestSupport.Messaging;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Metrics;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// FR-02, UC-01, IADR-0023: 定時系統の合流。InformationCollected 購読 → watchlist 巡回 → 開場銘柄のみ判断 → TradeDecisionMade 発行。
//
// ADR-0013, IADR-0129, #354: MassTransit のテストハーネス（AddMassTransitTestHarness + harness.Consumed/Published）
// から Wolverine.Tracking（TrackActivity + session.Executed/Sent）へ移行した。表明の意味は同じ。
public class InformationCollectedConsumerTests
{
    private const string BuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30}""";

    private sealed class FakeLlm(string output) : ILlmCompletionClient
    {
        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default) =>
            Task.FromResult(output);
    }
    // 特定銘柄でのみ例外を投げる LLM（銘柄はプロンプトに含まれる）。銘柄別の失敗分離を検証する。
    private sealed class ThrowingForSymbolLlm(string badSymbol, string output) : ILlmCompletionClient
    {
        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default) =>
            prompt.Contains(badSymbol, StringComparison.Ordinal)
                ? throw new InvalidOperationException($"LLM 障害（{badSymbol}）")
                : Task.FromResult(output);
    }
    private sealed class FakePolicy : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) =>
            Task.FromResult<DailyPolicy?>(new(new DateOnly(2026, 7, 10), "押し目買い"));
    }
    private sealed class FakeSizing : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(new SizingContext(
            100_000m, 100_000m, 100_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits()));
    }
    private sealed class FakeWatchlist(params WatchedSymbol[] symbols) : IWatchlistProvider
    {
        public Task<IReadOnlyList<WatchedSymbol>> GetWatchlistAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WatchedSymbol>>(symbols);
    }
    private sealed class CalendarStub(bool open) : IMarketCalendar
    {
        public bool IsOpen(Market market, DateTimeOffset instant) => open;
    }

    private const string ServiceName = "ai-stock-trading.trade-decision-service";

    private static Task<IHost> BuildAsync(
        IWatchlistProvider watchlist, IMarketCalendar calendar, ILlmCompletionClient? llm = null) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton(calendar);
                opts.Services.AddSingleton(watchlist);
                opts.Services.AddSingleton(llm ?? new FakeLlm(BuyJson));
                opts.Services.AddSingleton<IDailyPolicyProvider, FakePolicy>();
                opts.Services.AddSingleton<ISizingContextProvider, FakeSizing>();
                opts.Services.AddScoped<AppSvc>();
                // NFR-07, #287, IADR-0255: 業務メトリクスはハンドラの**必須依存**である。
                // 本番では AddAiStockTradingObservability が登録する（BusinessMetricsWiringTests が固定）。
                opts.Services.AddSingleton<BusinessMetrics>();

                opts.UseAiStockTradingRabbitMq(
                    ServiceName, "amqp://guest:guest@localhost:5672",
                    typeof(InformationCollectedHandler).Assembly);
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    // #257, IADR-0107: 本スイートは通貨換算の影響を分離するため基準通貨（日本株）の監視銘柄を用いる
    // （外貨建てはレート未解決だと見送りになる。換算そのものの検証は Application テストと FxWiringTests が担う）。

    [Fact]
    public async Task 開場中は監視銘柄について判断し_TradeDecisionMade_を発行する()
    {
        using var host = await BuildAsync(
            new FakeWatchlist(new WatchedSymbol("AAPL", Market.UnitedStates)),
            new CalendarStub(open: true));

        var session = await host.TrackActivityForTest()
            .InvokeMessageAndWaitAsync(new InformationCollected(Guid.NewGuid(), 3, DateTimeOffset.UtcNow));

        session.Executed.MessagesOf<InformationCollected>().Should().NotBeEmpty();
        session.Sent.MessagesOf<TradeDecisionMade>().Should().NotBeEmpty();
        var decision = session.Sent.MessagesOf<TradeDecisionMade>().First();
        decision.Intent.Symbol.Should().Be("AAPL");

        await host.StopAsync();
    }

    [Fact]
    public async Task 休場日は判断せず発行しない()
    {
        using var host = await BuildAsync(
            new FakeWatchlist(new WatchedSymbol("AAPL", Market.UnitedStates)),
            new CalendarStub(open: false));

        var session = await host.TrackActivityForTest()
            .InvokeMessageAndWaitAsync(new InformationCollected(Guid.NewGuid(), 3, DateTimeOffset.UtcNow));

        session.Executed.MessagesOf<InformationCollected>().Should().NotBeEmpty();
        session.Sent.MessagesOf<TradeDecisionMade>().Should().BeEmpty();

        await host.StopAsync();
    }

    [Fact]
    public async Task 一銘柄の失敗は他銘柄の処理を止めない_重複発注防止()
    {
        // IADR-0023: 1 銘柄の判断失敗でサイクル全体を再配送させず、他銘柄は継続して発行する。
        using var host = await BuildAsync(
            new FakeWatchlist(
                new WatchedSymbol("BADSYM", Market.UnitedStates),
                new WatchedSymbol("AAPL", Market.UnitedStates)),
            new CalendarStub(open: true),
            new ThrowingForSymbolLlm("BADSYM", BuyJson));

        var session = await host.TrackActivityForTest()
            .InvokeMessageAndWaitAsync(new InformationCollected(Guid.NewGuid(), 1, DateTimeOffset.UtcNow));

        session.Executed.MessagesOf<InformationCollected>().Should().NotBeEmpty();
        session.Sent.MessagesOf<TradeDecisionMade>().Should().NotBeEmpty();
        // AAPL の 1 件のみ発行される（BADSYM は例外で分離・スキップ）。
        session.Sent.MessagesOf<TradeDecisionMade>().Should().ContainSingle();
        session.Sent.MessagesOf<TradeDecisionMade>().First().Intent.Symbol.Should().Be("AAPL");

        await host.StopAsync();
    }

    [Fact]
    public async Task 監視銘柄が空なら発行しない()
    {
        using var host = await BuildAsync(new FakeWatchlist(), new CalendarStub(open: true));

        var session = await host.TrackActivityForTest()
            .InvokeMessageAndWaitAsync(new InformationCollected(Guid.NewGuid(), 3, DateTimeOffset.UtcNow));

        session.Executed.MessagesOf<InformationCollected>().Should().NotBeEmpty();
        session.Sent.MessagesOf<TradeDecisionMade>().Should().BeEmpty();

        await host.StopAsync();
    }

    // NFR-07, #287, IADR-0255: 定時系統でも判断回数・レイテンシが実際に刻まれる（肯定形）。
    // trigger タグで定時（scheduled）と価格変動（price-movement）を読み分けられることを固定する
    // —— 分けられないと「定時サイクルだけが止まっている」を検知できない。
    [Fact]
    public async Task 定時系統の判断で業務メトリクスが実際に刻まれる()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var host = await BuildAsync(
            new FakeWatchlist(new WatchedSymbol("AAPL", Market.UnitedStates)),
            new CalendarStub(open: true));

        await host.TrackActivityForTest()
            .InvokeMessageAndWaitAsync(new InformationCollected(Guid.NewGuid(), 3, DateTimeOffset.UtcNow));

        capture.TagValuesOf(BusinessMetricNames.TradeCycleDecisions, BusinessMetricNames.TagTrigger)
            .Should().Contain(BusinessMetrics.TriggerScheduled);
        capture.TagValuesOf(BusinessMetricNames.TradeCycleDecisions, BusinessMetricNames.TagAction)
            .Should().Contain("buy");
        capture.ValuesOf(BusinessMetricNames.TradeCycleDecisionDurationMs).Should().NotBeEmpty();

        await host.StopAsync();
    }
}
