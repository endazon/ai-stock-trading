using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.TradeDecision.Application.Adapters;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Infrastructure.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using AppSvc = AiStockTrading.TradeDecision.Application.Services.TradeDecisionService;

namespace AiStockTrading.TradeDecision.Infrastructure.Tests;

// FR-04, UC-02: PriceMovementDetected 購読 → 判断成立時のみ TradeDecisionMade を発行することを検証する。
//
// ADR-0013, IADR-0129, #354: MassTransit のテストハーネス（AddMassTransitTestHarness + harness.Consumed/Published）
// から Wolverine.Tracking（TrackActivity + session.Executed/Sent）へ移行した。表明の意味は同じ。
// 本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す。
public class PriceMovementDetectedConsumerTests
{
    private sealed class FakeLlm(string output) : ILlmCompletionClient
    {
        public Task<string> CompleteAsync(string prompt, string? model = null, CancellationToken ct = default) =>
            Task.FromResult(output);
    }
    private sealed class FakePolicy(DailyPolicy? p) : IDailyPolicyProvider { public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(p); }
    private sealed class FakeSizing : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(new SizingContext(
            100_000m, 100_000m, 100_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits()));
    }

    private const string BuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30}""";

    // 判断経路の検証のため既定は常に開場のカレンダー（実時刻だと週末で揺れるため）。休場ケースは open:false を注入する。
    private sealed class CalendarStub(bool open) : IMarketCalendar
    {
        public bool IsOpen(Market market, DateTimeOffset instant) => open;
    }

    private const string ServiceName = "ai-stock-trading.trade-decision-service";

    private static Task<IHost> BuildAsync(string llmOutput, DailyPolicy? policy, bool open = true) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton<IMarketCalendar>(new CalendarStub(open));
                opts.Services.AddSingleton<ILlmCompletionClient>(new FakeLlm(llmOutput));
                opts.Services.AddSingleton<IDailyPolicyProvider>(new FakePolicy(policy));
                opts.Services.AddSingleton<ISizingContextProvider, FakeSizing>();
                opts.Services.AddScoped<AppSvc>();

                opts.UseAiStockTradingRabbitMq(
                    ServiceName, "amqp://guest:guest@localhost:5672",
                    typeof(PriceMovementDetectedHandler).Assembly);
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    // #257, IADR-0107: 本スイートは通貨換算の影響を分離するため基準通貨（日本株）の銘柄を用いる
    // （外貨建てはレート未解決だと見送りになる。換算そのものの検証は Application テストと FxWiringTests が担う）。
    private static PriceMovementDetected Trigger() =>
        new(Guid.NewGuid(), "7203", Market.Japan, 1_040m, 1_000m, 0.04m, DateTimeOffset.UtcNow);

    [Fact]
    public async Task 方針ありでBuy判断ならTradeDecisionMadeを発行する()
    {
        using var host = await BuildAsync(BuyJson, new DailyPolicy(new DateOnly(2026, 7, 10), "押し目買い"));

        var session = await host.TrackActivity().InvokeMessageAndWaitAsync(Trigger());

        session.Executed.MessagesOf<PriceMovementDetected>().Should().NotBeEmpty();
        session.Sent.MessagesOf<TradeDecisionMade>().Should().NotBeEmpty();
        var decision = session.Sent.MessagesOf<TradeDecisionMade>().First();
        decision.Intent.PositionEffect.Should().Be(PositionEffect.Open);

        // IADR-0129 決定 2: 宛先はメッセージ型ごとの共有 fanout exchange（RiskManagement / MarketMonitor / Audit が bind する）。
        session.Sent.Envelopes().Should().Contain(e =>
            e.Message is TradeDecisionMade
            && e.Destination!.ToString()
                == "rabbitmq://exchange/AiStockTrading.Shared.Contracts.Events.TradeDecisionMade");

        await host.StopAsync();
    }

    [Fact]
    public async Task 休場日は判断せず発行しない_祝日ガード()
    {
        // FR-02, IADR-0023: イベント駆動系統も市場カレンダー閉場ならスキップする。
        using var host = await BuildAsync(BuyJson, new DailyPolicy(new DateOnly(2026, 7, 10), "押し目買い"), open: false);

        var session = await host.TrackActivity().InvokeMessageAndWaitAsync(Trigger());

        session.Executed.MessagesOf<PriceMovementDetected>().Should().NotBeEmpty();
        session.Sent.MessagesOf<TradeDecisionMade>().Should().BeEmpty();

        await host.StopAsync();
    }

    [Fact]
    public async Task 確定済み日報が無ければ発行しない()
    {
        // FR-07: 方針なし → 取引しない。
        using var host = await BuildAsync(BuyJson, policy: null);

        var session = await host.TrackActivity().InvokeMessageAndWaitAsync(Trigger());

        session.Executed.MessagesOf<PriceMovementDetected>().Should().NotBeEmpty();
        session.Sent.MessagesOf<TradeDecisionMade>().Should().BeEmpty();

        await host.StopAsync();
    }
}
