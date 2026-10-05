extern alias RiskManagementWorker;

using System.Collections.Concurrent;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.Steps;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// 🔴 FR-02, FR-10, NFR-02, #1169, IADR-0490: 定時サイクル（1 通で全銘柄を順に判断する）が打ち切られたり再配送されたりしても、
// 判断を二重に発行しない・同じ判断は同じ DecisionId で出る（下流の DecisionId の冪等が止められる形にする）。
// 1 銘柄の遅延はその銘柄の失敗として分離し、サイクル全体の打ち切りにしない。
//
// 実際の受信の経路（Wolverine の実行器。実行時間の上限・再試行・失敗した試行の発行の破棄）を通すため、
// InformationCollected をローカルキューへ流す（InvokeMessageAndWaitAsync は実行時間の上限を掛けない経路である）。
public class ScheduledCycleRedeliveryTests
{
    private const string ServiceName = "ai-stock-trading.trade-decision-service";
    private const string CycleQueue = "scheduled-cycle-under-test";

    private const string BuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30}""";

    private static bool IsTarget(string prompt, string symbol) =>
        // #1034, IADR-0440: プロンプトは監視銘柄の全件を載せるため、「判断対象」の行で見分ける。
        prompt.Contains($"判断対象の {symbol}（", StringComparison.Ordinal);

    // 指定した銘柄の**最初の 1 回だけ**応答しない（token の取り消しまで待つ）LLM。2 回目以降と他の銘柄は即答する。
    // 応答しない上流＋打ち切りの観測（IADR-0379 決定 2）であり、壁時計どうしの競争にはしない。
    private sealed class HangOnceLlm(string hangSymbol) : ILlmCompletionClient
    {
        private int _hung;

        public ConcurrentQueue<string> Targets { get; } = new();

        public int CancelledHangs;

        public async Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            var target = IsTarget(prompt, hangSymbol) ? hangSymbol : "other";
            Targets.Enqueue(target);
            if (target == hangSymbol && Interlocked.Exchange(ref _hung, 1) == 0)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref CancelledHangs);
                    throw;
                }
            }

            return BuyJson;
        }
    }

    // 指定した銘柄では**毎回**応答しない LLM（銘柄の締め切りが無ければサイクルごと止まる）。
    private sealed class AlwaysHangLlm(string hangSymbol) : ILlmCompletionClient
    {
        public int CancelledHangs;

        public async Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            if (!IsTarget(prompt, hangSymbol))
                return BuyJson;

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref CancelledHangs);
                throw;
            }

            return BuyJson;
        }
    }

    private sealed class InstantLlm : ILlmCompletionClient
    {
        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default) =>
            Task.FromResult(BuyJson);
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
        public Task<IReadOnlyList<WatchedSymbol>?> GetWatchlistAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WatchedSymbol>?>(symbols);

        public Task<IReadOnlyList<WatchedSymbol>?> GetAuthoritativeWatchlistAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WatchedSymbol>?>(symbols);
    }

    private sealed class OpenCalendar : IMarketCalendar
    {
        public bool IsOpen(Market market, DateTimeOffset instant) => true;
    }

    // 試験だけでサイクルの上限を短くする（本番の上限の導出は ScheduledCycleBudgetTests・ScheduledCycleTimeoutCompositionTests が固定する）。
    private sealed class ShortCycleTimeout(int seconds) : IHandlerPolicy
    {
        public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
        {
            foreach (var chain in chains.Where(c => c.MessageType == typeof(InformationCollected)))
                chain.ExecutionTimeoutInSeconds = seconds;
        }
    }

    // 警告の行を拾うロガー（監視銘柄が前提を超えたときの告知）。
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }

    private static readonly WatchedSymbol Aapl = new("AAPL", Market.UnitedStates);
    private static readonly WatchedSymbol Msft = new("MSFT", Market.UnitedStates);

    private static Task<IHost> BuildAsync(
        ILlmCompletionClient llm,
        ScheduledCycleBudget budget,
        RecordingTradeDecisionFailureReporter failures,
        int? cycleTimeoutSeconds = null,
        CapturingLoggerProvider? logs = null,
        params WatchedSymbol[] watchlist) =>
        Host.CreateDefaultBuilder()
            .ConfigureLogging(l =>
            {
                if (logs is not null)
                    l.AddProvider(logs);
            })
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton<IMarketCalendar>(new OpenCalendar());
                opts.Services.AddSingleton<IWatchlistProvider>(new FakeWatchlist(watchlist));
                opts.Services.AddSingleton(llm);
                opts.Services.AddSingleton<IDailyPolicyProvider, FakePolicy>();
                opts.Services.AddSingleton<ISizingContextProvider, FakeSizing>();
                opts.Services.AddScoped<AppSvc>();
                opts.Services.AddSingleton<BusinessMetrics>();
                opts.Services.AddSingleton<NewsCollectionStatusStore>();
                opts.Services.AddSingleton<ITradeDecisionFailureReporter>(failures);
                opts.Services.AddSingleton(budget);

                opts.UseAiStockTradingRabbitMq(
                    ServiceName, "amqp://guest:guest@localhost:5672",
                    typeof(InformationCollectedHandler).Assembly);
                opts.StubAllExternalTransports();

                // 受信の実行器を通す（実行時間の上限・共通の再試行〔2s/10s/30s〕・失敗した試行の発行の破棄が効く経路）。
                opts.PublishMessage<InformationCollected>().ToLocalQueue(CycleQueue);
                if (cycleTimeoutSeconds is { } seconds)
                    opts.Policies.Add(new ShortCycleTimeout(seconds));
            })
            .StartAsync();

    // 本番の既定と同じ導出（1 銘柄 90 秒・サイクル 960 秒）。下の試験の時間の中では締め切りは働かない。
    private static ScheduledCycleBudget ProductionLikeBudget() =>
        ScheduledCycleBudget.Derive(TimeSpan.FromSeconds(30), 2, ScheduledCycleBudget.DefaultMaxWatchedSymbols);

    // 🔴 T-10-2246（最重要・否定形）: サイクルが実行時間の上限で打ち切られて再試行されても、判断は銘柄ごとに 1 件だけ発行される。
    // 1 回目の試行で判断済みの銘柄（AAPL）の発行は送られずに捨てられ、再試行の判断が同じ DecisionId で 1 件だけ出る。
    // #1169 の実測（1 回目の MSFT の判断が下流へ届かず、再試行が新しい DecisionId で出した）を、上限を 2 秒にして再現する。
    [Fact]
    public async Task T_10_2246_打ち切られて再試行されたサイクルは判断を二重に発行しない()
    {
        var llm = new HangOnceLlm("MSFT");
        var failures = new RecordingTradeDecisionFailureReporter();
        using var host = await BuildAsync(llm, ProductionLikeBudget(), failures, cycleTimeoutSeconds: 2, watchlist: [Aapl, Msft]);
        var message = new InformationCollected(Guid.NewGuid(), 3, DateTimeOffset.UtcNow);

        var session = await host.TrackActivityForTest()
            .DoNotAssertOnExceptionsDetected()
            .PublishMessageAndWaitAsync(message);

        llm.CancelledHangs.Should().Be(1, "前提: 1 回目の試行がサイクルの上限で打ち切られた");
        llm.Targets.Count(t => t == "other").Should().BeGreaterThanOrEqualTo(2, "前提: AAPL は 1 回目と再試行で 2 回判断された");

        var decisions = session.Sent.MessagesOf<TradeDecisionMade>().ToList();
        decisions.Select(d => d.Intent.Symbol).Should().BeEquivalentTo(
            ["AAPL", "MSFT"], "銘柄ごとに 1 件だけ（1 回目の試行の AAPL の発行は捨てられる）");
        decisions.Single(d => d.Intent.Symbol == "AAPL").DecisionId
            .Should().Be(ScheduledDecisionIds.For(message.EventId, "AAPL", Market.UnitedStates)!.Value);
        decisions.Single(d => d.Intent.Symbol == "MSFT").DecisionId
            .Should().Be(ScheduledDecisionIds.For(message.EventId, "MSFT", Market.UnitedStates)!.Value);
        failures.Calls.Should().BeEmpty("サイクルの打ち切りは銘柄の失敗ではない（銘柄の catch で握り潰さず再試行へ伝える）");

        await host.StopAsync();
    }

    // 🔴 T-10-2245（否定形）: 同じ起点イベントが再配送されて**発行まで**もう一度走っても（発行の後・受信の完了の前に落ちた場合）、
    // 判断は同じ DecisionId で出る＝下流の DecisionId の冪等（承認済みの再配送は再審査しない・発注は DecisionId で一意予約）が止める。
    // 別の起点イベント（次の巡回）は別の DecisionId になる（次の巡回の判断を再配送と見て捨てない）。
    [Fact]
    public async Task T_10_2245_同じ起点イベントの再配送は同じDecisionIdで判断を出す()
    {
        var failures = new RecordingTradeDecisionFailureReporter();
        using var host = await BuildAsync(new InstantLlm(), ProductionLikeBudget(), failures, watchlist: [Aapl, Msft]);
        var message = new InformationCollected(Guid.NewGuid(), 3, DateTimeOffset.UtcNow);

        var first = await host.TrackActivityForTest().PublishMessageAndWaitAsync(message);
        var redelivered = await host.TrackActivityForTest().PublishMessageAndWaitAsync(message);
        var nextCycle = await host.TrackActivityForTest()
            .PublishMessageAndWaitAsync(message with { EventId = Guid.NewGuid() });

        var firstIds = first.Sent.MessagesOf<TradeDecisionMade>().Select(d => d.DecisionId).ToList();
        var redeliveredIds = redelivered.Sent.MessagesOf<TradeDecisionMade>().Select(d => d.DecisionId).ToList();
        var nextIds = nextCycle.Sent.MessagesOf<TradeDecisionMade>().Select(d => d.DecisionId).ToList();

        firstIds.Should().HaveCount(2);
        redeliveredIds.Should().BeEquivalentTo(firstIds, "再配送は同じ判断として下流の冪等に止められる");
        nextIds.Should().HaveCount(2).And.NotIntersectWith(firstIds, "次の巡回は新しい判断");

        await host.StopAsync();
    }

    // 🔴 T-10-2247: 1 銘柄が締め切りを超えても、その銘柄の失敗として分離し（最終の失敗を 1 件報告）、他の銘柄は発行する。
    // サイクルは成功で終わり、再試行（＝全銘柄の判断のやり直し）にならない。
    [Fact]
    public async Task T_10_2247_1銘柄の締め切り超過はその銘柄の失敗として分離しサイクルを再試行させない()
    {
        var llm = new AlwaysHangLlm("MSFT");
        var failures = new RecordingTradeDecisionFailureReporter();
        // 1 銘柄の締め切り ＝ 1 秒 × 1 回 ＋ 2 秒 ＝ 3 秒。サイクルの上限は既定（60 秒）のまま。
        var budget = ScheduledCycleBudget.Derive(TimeSpan.FromSeconds(1), 1, 2, TimeSpan.FromSeconds(2), TimeSpan.Zero);
        using var host = await BuildAsync(llm, budget, failures, watchlist: [Msft, Aapl]);

        var session = await host.TrackActivityForTest()
            .PublishMessageAndWaitAsync(new InformationCollected(Guid.NewGuid(), 3, DateTimeOffset.UtcNow));

        llm.CancelledHangs.Should().Be(1, "MSFT は 1 回だけ判断され、締め切りで止まった（再試行で判断し直さない）");
        session.Sent.MessagesOf<TradeDecisionMade>().Should().ContainSingle()
            .Which.Intent.Symbol.Should().Be("AAPL", "後続の銘柄は止まらない");
        failures.Calls.Should().ContainSingle().Which.Symbol.Should().Be("MSFT");
        failures.Calls.Single().CycleTrigger.Should().Be(BusinessMetrics.TriggerScheduled);

        await host.StopAsync();
    }

    // T-10-2248（否定形）: 起点イベントの ID が空なら DecisionId を決定的に導かない（毎回新規）。空から導くと、その銘柄の以後の
    // 定時判断がすべて同じ ID になり、下流が 2 回目以降を再配送として捨てる（取引が黙って止まる）。
    [Fact]
    public async Task T_10_2248_起点イベントのIDが空なら決定的なDecisionIdを使わない()
    {
        var failures = new RecordingTradeDecisionFailureReporter();
        using var host = await BuildAsync(new InstantLlm(), ProductionLikeBudget(), failures, watchlist: [Aapl]);
        var message = new InformationCollected(Guid.Empty, 3, DateTimeOffset.UtcNow);

        var first = await host.TrackActivityForTest().PublishMessageAndWaitAsync(message);
        var second = await host.TrackActivityForTest().PublishMessageAndWaitAsync(message);

        var a = first.Sent.MessagesOf<TradeDecisionMade>().Should().ContainSingle().Which.DecisionId;
        var b = second.Sent.MessagesOf<TradeDecisionMade>().Should().ContainSingle().Which.DecisionId;
        b.Should().NotBe(a);
        a.Should().NotBe(Guid.Empty);

        await host.StopAsync();
    }

    // T-10-2249: 監視銘柄が上限の前提の数を超えたら、サイクルが上限に達し得ることを警告で告げる（判断は止めない）。前提以内なら告げない。
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task T_10_2249_監視銘柄が前提を超えたら警告する(int maxWatched, bool expectWarning)
    {
        var logs = new CapturingLoggerProvider();
        var failures = new RecordingTradeDecisionFailureReporter();
        var budget = ScheduledCycleBudget.Derive(TimeSpan.FromSeconds(30), 2, maxWatched);
        using var host = await BuildAsync(new InstantLlm(), budget, failures, logs: logs, watchlist: [Aapl, Msft]);

        var session = await host.TrackActivityForTest()
            .PublishMessageAndWaitAsync(new InformationCollected(Guid.NewGuid(), 3, DateTimeOffset.UtcNow));

        session.Sent.MessagesOf<TradeDecisionMade>().Should().HaveCount(2, "警告しても判断は止めない");
        logs.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("上限の前提", StringComparison.Ordinal))
            .Should().Be(expectWarning);

        await host.StopAsync();
    }
}
