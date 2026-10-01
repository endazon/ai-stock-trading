extern alias RiskManagementWorker;

using System.Diagnostics;
using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// FR-02, FR-10, #1133, IADR-0469: 判断の現在値の照会で Finnhub が応答を返さないとき（2026-09-30 夜、TLS ハンドシェイクの切断で
// 1 判断が 18.5 秒待った）。本物の部品（Finnhub の受け手 → 現在値の口）を通し、打ち切りが判断の既存の失敗の経路
// 「現在値が取れない」（CurrentPriceUnavailable の見送り）に落ちること、呼び出し側の停止は見送りに化けないことを見る。
// 判断のサービス本体は変えていない（変えたのは受け手の打ち切りの写し方だけ）。
public class FinnhubQuoteTimeoutDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 15, 56, 0, TimeSpan.Zero);
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 9, 30), "米国株の押し目買い方針");

    private const string BuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30}""";

    // 🔴 T-10-1865: 応答が返らない Finnhub は、打ち切りの時間で「現在値が取れない」の見送りになり、例外で判断を落とさない。
    [Fact]
    public async Task T_10_1865_応答が返らない現在値の照会は打ち切りの時間で現在値なしの見送りになる()
    {
        using var handler = new HangingHandler();
        var llm = new CountingLlm();
        var reporter = new RecordingSkipReporter();
        var service = Create(handler, TimeSpan.FromMilliseconds(200), llm, reporter);

        var watch = Stopwatch.StartNew();
        var decision = await service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);
        watch.Stop();

        decision.Should().BeNull("現在値が取れない新規建ては見送る（安全側）");
        reporter.Reports.Should().ContainSingle().Which.Should().Be(DecisionSkipReason.CurrentPriceUnavailable);
        llm.Calls.Should().Be(0, "現在値が取れなければ LLM を呼ぶ前に見送る");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "打ち切り（200 ms）で返り、既定の 100 秒は待たない");
    }

    // 🔴 T-10-1868: 現在値の応答を待つ間に呼び出し側が止めたら、見送りに化けず OperationCanceledException として伝わる。
    [Fact]
    public async Task T_10_1868_現在値を待つ間の呼び出し側の停止は見送りに化けず伝わる()
    {
        using var handler = new HangingHandler();
        var reporter = new RecordingSkipReporter();
        var service = Create(handler, TimeSpan.FromSeconds(30), new CountingLlm(), reporter);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var call = service.DecideAsync(Trigger(), cts.Token);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var act = async () => await call;
        await act.Should().ThrowAsync<OperationCanceledException>();
        reporter.Reports.Should().BeEmpty("停止は見送りの理由ではない");
    }

    private static PriceMovementDetected Trigger() =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, 1_040m, 1_000m, 0.04m, Now);

    private static AppSvc Create(
        HttpMessageHandler handler, TimeSpan timeout, ILlmCompletionClient llm, RecordingSkipReporter reporter)
    {
        var source = new FinnhubMarketDataSource(
            new FinnhubQuoteClient(
                new HttpClient(handler) { Timeout = timeout }, "key", new PassThroughRateLimiter(), NullLogger.Instance),
            NullLogger<FinnhubMarketDataSource>.Instance);
        var currentPrice = new MarketDataCurrentPriceProvider(
            source, enabled: true, new FakeClock(), TimeSpan.FromSeconds(300),
            NullLogger<MarketDataCurrentPriceProvider>.Instance);

        return new(llm, new FakePolicy(Policy), new FakeSizing(Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance,
            retrieval: null, options: null, profitability: null, profitabilityOptions: null,
            unconfirmedNotifier: null, currentPrice: currentPrice, fxRate: null, heldPosition: new FakeHeld(),
            retrievalSourcePolicy: null, statusNotifier: null, screeningReporter: null,
            skipReporter: reporter);
    }

    private static SizingContext Context() =>
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class CountingLlm : ILlmCompletionClient
    {
        public int Calls { get; private set; }

        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(BuyJson);
        }
    }

    private sealed class FakePolicy(DailyPolicy? policy) : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(policy);
    }

    private sealed class FakeSizing(SizingContext ctx) : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(ctx);
    }

    // 保有なし（照会は成功）。現在値の経路だけを見るため、保有・未約定の照会は平常の値を返す。
    private sealed class FakeHeld : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<int?>(0);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<HeldPosition?>(HeldPosition.None);

        public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
            string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<WorkingEntryOrders?>(WorkingEntryOrders.None);
    }

    private sealed class RecordingSkipReporter : IDecisionSkipReporter
    {
        public List<DecisionSkipReason> Reports { get; } = [];

        public void Report(string trigger, DecisionSkipReason reason) => Reports.Add(reason);
    }

    private sealed class PassThroughRateLimiter : IRateLimiter
    {
        public Task WaitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    /// <summary>応答を返さない（取り消されるまで待ち続ける）ハンドラ。TLS ハンドシェイクで止まる相手の代わり。</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("到達しない");
        }
    }
}
