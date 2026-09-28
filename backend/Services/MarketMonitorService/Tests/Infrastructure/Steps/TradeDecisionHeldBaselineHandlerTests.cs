using MarketMonitorService.Domain;
using MarketMonitorService.Features.MarketMonitor;
using MarketMonitorService.Infrastructure.ExternalServices;
using MarketMonitorService.Infrastructure.Persistence;
using MarketMonitorService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace MarketMonitorService.Tests;

// 🔴 UC-02, FR-03, #1077, IADR-0452 決定2: AI 判断後の見送り（TradeDecisionHeld）で急変の基準値が判断時点価格へ進む。
//
// 以前は TradeDecisionMade（発注意図あり）だけが基準値を進め、稼働 PoC（2026-09-28）で全件 Hold が続いた間、
// 基準値が作られず UC-02 が一度も発火しなかった。計画の基準点は「前回 AI 判断を行った時点の価格」で、Hold も AI 判断である。
public class TradeDecisionHeldBaselineHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 14, 0, 0, TimeSpan.Zero);

    private static TradeDecisionHeld Held(decimal price, string symbol = "AAPL") =>
        new(Guid.NewGuid(), symbol, Market.UnitedStates, price, "LlmHold", Now, "scheduled");

    private static Task<IHost> BuildHostAsync(IPriceBaselineStore baselines) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(baselines);
                opts.Discovery.IncludeAssembly(typeof(TradeDecisionHeldBaselineHandler).Assembly);
                // 実ブローカへ接続しない（ローカル・CI ともに RabbitMQ を要求しない）。
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    [Fact]
    public async Task 判断後の見送りで対象銘柄の基準値を判断時点価格へ更新する()
    {
        var baselines = new InMemoryPriceBaselineStore();
        using var host = await BuildHostAsync(baselines);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Held(212.5m));

        session.Executed.MessagesOf<TradeDecisionHeld>().Should().NotBeEmpty();
        baselines.GetBaseline("AAPL", Market.UnitedStates).Should().Be(212.5m);

        await host.StopAsync();
    }

    // Hold が続けば基準値はそのたびに前進する（前回の Hold が基準点）。
    [Fact]
    public async Task Holdが続くと基準値は毎回の判断時点価格へ前進する()
    {
        var baselines = new InMemoryPriceBaselineStore();
        using var host = await BuildHostAsync(baselines);

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Held(1_000m));
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Held(1_020m));

        baselines.GetBaseline("AAPL", Market.UnitedStates).Should().Be(1_020m);

        await host.StopAsync();
    }

    // 受け側の守り: 0 以下の基準値は変動率の分母を壊す（誤発火より不更新）。既存の基準値を上書きしない。
    [Fact]
    public async Task 価格が正でなければ基準値を更新しない()
    {
        var baselines = new InMemoryPriceBaselineStore();
        baselines.SetBaseline("AAPL", Market.UnitedStates, 1_000m);
        using var host = await BuildHostAsync(baselines);

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Held(0m));

        baselines.GetBaseline("AAPL", Market.UnitedStates).Should().Be(1_000m);

        await host.StopAsync();
    }

    // 🔴 #1077 の受け入れ（端から端）: **Hold が続いても急変が発火し得る。**
    // 判断（Hold）→ 基準値 1,000 → 小さな値動き（+2%）では発火しない → 次の判断（Hold）で基準値 1,020 →
    // その後 +3.9% の急変で PriceMovementDetected が出る（基準値は直近の Hold 時点の価格）。
    [Fact]
    public async Task Holdが続いても基準値が進み急変が発火し得る()
    {
        var baselines = new InMemoryPriceBaselineStore();
        var market = new FakeMarketDataSource();
        var monitor = new MarketMonitorAppService(
            new InMemoryMonitoredSymbolStore(new MarketMonitorSettings
            {
                MovementThresholdRatio = 0.03m,
                Cooldown = TimeSpan.FromMinutes(15),
                MonitoredSymbols = [new MonitoredSymbol("AAPL", Market.UnitedStates)],
            }),
            new InMemoryPositionStore(), baselines, new InMemoryCooldownStore(), market,
            new FakeSchedule(open: true), new FakeClock(Now));
        using var host = await BuildHostAsync(baselines);

        // 判断前（基準値なし）は変動を判定しない（計画: 前回 AI 判断が無ければ基準点が無い）。
        market.Set("AAPL", Market.UnitedStates, 1_000m);
        (await monitor.EvaluateRoundAsync(TestContext.Current.CancellationToken)).PriceMovements.Should().BeEmpty();

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Held(1_000m));
        market.Set("AAPL", Market.UnitedStates, 1_020m);
        (await monitor.EvaluateRoundAsync(TestContext.Current.CancellationToken)).PriceMovements
            .Should().BeEmpty("+2% は閾値 3% に届かない");

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Held(1_020m));
        market.Set("AAPL", Market.UnitedStates, 1_060m);
        var movement = (await monitor.EvaluateRoundAsync(TestContext.Current.CancellationToken)).PriceMovements
            .Should().ContainSingle("直近の Hold 時点 1,020 から +3.9%").Subject;
        movement.BaselinePrice.Should().Be(1_020m);

        await host.StopAsync();
    }

    // キューの分離（IADR-0129 決定1）: 市場監視は自前のキューで全件受け取る（他サービスと取り合わない）。
    [Fact]
    public void 判断後の見送り購読のキュー名は市場監視のサービス名を前置する()
    {
        WolverineExtensions.QueueNameFor("ai-stock-trading.market-monitor-service", typeof(TradeDecisionHeld))
            .Should().Be("ai-stock-trading.market-monitor-service.TradeDecisionHeld");
    }
}
