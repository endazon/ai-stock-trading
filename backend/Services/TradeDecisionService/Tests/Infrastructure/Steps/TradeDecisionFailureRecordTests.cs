extern alias RiskManagementWorker;

using System.Text.Json;
using RiskManagementWorker::RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using TradeDecisionService.Infrastructure.Steps;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// 🔴 T-10-2180〜T-10-2182・T-10-2184, NFR, FR-04, FR-11, #1111, IADR-0483:
// 取引判断の**最中の例外**は、再試行の後の**最終の失敗だけ**を 1 件、監査台帳へ残す。載せるのは型名・発生源・銘柄・時刻だけで、
// メッセージとスタックは載せない（裁定 2026-10-02「最小限で残す」）。発行はランタイムの MessageBus から行う。
//
// 例外は秘密（API キーの形）と口座 ID を本文・内側の例外に持たせ、スタックも持つ（投げてから使う）。陰性の表明はそれらが
// 台帳へ渡る事実（JSON）とログのどこにも現れないことである。
public class TradeDecisionFailureRecordTests
{
    private const string ServiceName = "ai-stock-trading.trade-decision-service";
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 14, 0, 0, TimeSpan.Zero);

    // 陰性の試験で探す断片（どれも台帳とログに出てはならない）。
    private const string SecretKey = "sk-live-SECRET-1111";
    private const string AccountId = "281756479345";
    private const string InnerSecret = "Bearer eyJhbGciOiJIUzI1NiJ9.SECRET";
    private static readonly string SecretMessage = $"照会に失敗 api_key={SecretKey} 口座 {AccountId}";

    private const string BuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30}""";

    public sealed class SecretBearingException(string message, Exception inner) : InvalidOperationException(message, inner);

    public sealed class GenericFailure<T>(string message) : Exception(message);

    // スタックを持つ例外を作る（投げてから捕まえる）。
    private static SecretBearingException ThrownSecretException()
    {
        try
        {
            ThrowSecret();
        }
        catch (SecretBearingException ex)
        {
            return ex;
        }

        throw new InvalidOperationException("到達しない");
    }

    private static void ThrowSecret() =>
        throw new SecretBearingException(SecretMessage, new HttpRequestException(InnerSecret));

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FakePolicy : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) =>
            Task.FromResult<DailyPolicy?>(new(new DateOnly(2026, 10, 2), "押し目買い"));
    }

    private sealed class FakeSizing : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(new SizingContext(
            100_000m, 100_000m, 100_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits()));
    }

    // 判断の最中に（LLM の手前のサイジング文脈の取得で）秘密つきの例外を投げる。
    private sealed class ThrowingSizing : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default)
        {
            ThrowSecret();
            return null!;
        }
    }

    private sealed class FakeLlm(string output) : ILlmCompletionClient
    {
        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default) =>
            Task.FromResult(output);
    }

    // 特定銘柄の判断でだけ秘密つきの例外を投げる LLM（銘柄は「判断対象の」行で見分ける。InformationCollectedConsumerTests と同じ）。
    private sealed class ThrowingForSymbolLlm(string badSymbol) : ILlmCompletionClient
    {
        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            if (prompt.Contains($"判断対象の {badSymbol}（", StringComparison.Ordinal))
                ThrowSecret();
            return Task.FromResult(BuyJson);
        }
    }

    private sealed class OpenCalendar : IMarketCalendar
    {
        public bool IsOpen(Market market, DateTimeOffset instant) => true;
    }

    private sealed class FakeWatchlist(params WatchedSymbol[] symbols) : IWatchlistProvider
    {
        public Task<IReadOnlyList<WatchedSymbol>?> GetWatchlistAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WatchedSymbol>?>(symbols);

        public Task<IReadOnlyList<WatchedSymbol>?> GetAuthoritativeWatchlistAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WatchedSymbol>?>(symbols);
    }

    // ログの本文と例外を記録する（陰性の表明に使う）。
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add($"{logLevel}: {formatter(state, exception)} {exception}");
        }
    }

    private static PriceMovementDetected Trigger() =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, 1_040m, 1_000m, 0.04m, Now);

    private static AppSvc DecisionService(ISizingContextProvider sizing, ILlmCompletionClient? llm = null) =>
        new(llm ?? new FakeLlm(BuyJson), new FakePolicy(), sizing, new FixedClock(), NullLogger<AppSvc>.Instance);

    private static PriceMovementDetectedHandler PriceHandler(AppSvc service, ITradeDecisionFailureReporter reporter) =>
        new(service, new OpenCalendar(), new FixedClock(), new BusinessMetrics(), reporter,
            NullLogger<PriceMovementDetectedHandler>.Instance);

    private static Task<IHost> StartBareHostAsync() =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // 本番と同じ配線（キュー名・fan-out）を用い、送信先だけ stub へ倒す。
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    // Program.cs と同じ形の発行の実装（ランタイムの MessageBus から出す）。
    private static PublishingTradeDecisionFailureReporter RuntimeReporter(IHost host, ILogger<PublishingTradeDecisionFailureReporter>? logger = null)
    {
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        return new PublishingTradeDecisionFailureReporter(
            e => new MessageBus(runtime).PublishAsync(e), new FixedClock(),
            logger ?? NullLogger<PublishingTradeDecisionFailureReporter>.Instance);
    }

    private static void AssertNoSecret(string text, string because)
    {
        text.Should().NotContain(SecretKey, because);
        text.Should().NotContain(AccountId, because);
        text.Should().NotContain("SECRET", because);
        text.Should().NotContain("照会に失敗", because);
        text.Should().NotContain(nameof(ThrowSecret), because + "（スタックの行）");
        text.Should().NotContain(" at ", because + "（スタックの行）");
    }

    // ---- T-10-2180: 価格変動の経路は最後の配送の失敗だけを 1 件渡す（途中の再試行では渡さない） ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task T_10_2180_価格変動の途中の再試行の失敗は渡さず_例外は投げ直す(int attempts)
    {
        using var host = await StartBareHostAsync();
        var reporter = new RecordingTradeDecisionFailureReporter();
        var handler = PriceHandler(DecisionService(new ThrowingSizing()), reporter);

        await Assert.ThrowsAsync<SecretBearingException>(() => handler.Handle(
            Trigger(), new MessageContext(host.Services.GetRequiredService<IWolverineRuntime>()),
            new Envelope { Attempts = attempts }, CancellationToken.None));

        reporter.Calls.Should().BeEmpty("再試行のたびには数えない（この失敗の後にまだ配送がある）");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)] // 退避先から配送回数を引き継いで戻された場合など。規則の枠の外で再び退避先へ行く
    public async Task T_10_2180_価格変動の最後の配送の失敗は1件だけ渡し_例外は投げ直す(int attempts)
    {
        using var host = await StartBareHostAsync();
        var reporter = new RecordingTradeDecisionFailureReporter();
        var handler = PriceHandler(DecisionService(new ThrowingSizing()), reporter);

        await Assert.ThrowsAsync<SecretBearingException>(() => handler.Handle(
            Trigger(), new MessageContext(host.Services.GetRequiredService<IWolverineRuntime>()),
            new Envelope { Attempts = attempts }, CancellationToken.None));

        var call = reporter.Calls.Should().ContainSingle().Which;
        call.CycleTrigger.Should().Be(BusinessMetrics.TriggerPriceMovement);
        call.Symbol.Should().Be("AAPL");
        call.Market.Should().Be(Market.UnitedStates);
        call.Exception.Should().BeOfType<SecretBearingException>();
    }

    [Fact]
    public async Task T_10_2180_1通のメッセージの初回から最後の配送まで失敗し続けても渡すのは1件()
    {
        using var host = await StartBareHostAsync();
        var reporter = new RecordingTradeDecisionFailureReporter();
        var handler = PriceHandler(DecisionService(new ThrowingSizing()), reporter);
        var message = Trigger();

        for (var attempt = 1; attempt <= WolverineExtensions.MaxDeliveryAttempts; attempt++)
        {
            await Assert.ThrowsAsync<SecretBearingException>(() => handler.Handle(
                message, new MessageContext(host.Services.GetRequiredService<IWolverineRuntime>()),
                new Envelope { Attempts = attempt }, CancellationToken.None));
        }

        reporter.Calls.Should().ContainSingle("1 件の失敗を再試行の回数だけ数えない");
    }

    [Fact]
    public async Task T_10_2180_最後の配送でも判断が成功すれば渡さない()
    {
        using var host = await StartBareHostAsync();
        var reporter = new RecordingTradeDecisionFailureReporter();
        var handler = PriceHandler(DecisionService(new FakeSizing()), reporter);

        await handler.Handle(
            Trigger(), new MessageContext(host.Services.GetRequiredService<IWolverineRuntime>()),
            new Envelope { Attempts = WolverineExtensions.MaxDeliveryAttempts }, CancellationToken.None);

        reporter.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task T_10_2180_本判断のキャンセルは最後の配送でも渡さない_否定形()
    {
        using var host = await StartBareHostAsync();
        var reporter = new RecordingTradeDecisionFailureReporter();
        var handler = PriceHandler(DecisionService(new ThrowingSizing()), reporter);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => handler.Handle(
            Trigger(), new MessageContext(host.Services.GetRequiredService<IWolverineRuntime>()),
            new Envelope { Attempts = WolverineExtensions.MaxDeliveryAttempts }, cts.Token));

        reporter.Calls.Should().BeEmpty("停止は判断の失敗ではない");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public void T_10_2180_配送回数が最大配送回数に達した配送だけを最後とみなす(int attempts, bool expected)
    {
        PriceMovementDetectedHandler.IsFinalDeliveryAttempt(attempts).Should().Be(expected);
        WolverineExtensions.MaxDeliveryAttempts.Should().Be(4, "共通の再試行は 2s/10s/30s の 3 回（IADR-0129 決定5）");
    }

    // 🔴 ハンドラが例外で終わる（ハンドラの IMessageBus の発行は捨てられる）のに、最終の失敗の事実は外へ出る。
    // ハンドラへ渡す bus は flush しない MessageContext（Wolverine が失敗の処理で捨てる送信の箱）で、報告はランタイムの MessageBus から出る。
    [Fact]
    public async Task T_10_2180_価格変動のハンドラが失敗で終わっても最終の失敗はランタイムから外へ出る()
    {
        using var host = await StartBareHostAsync();
        var logger = new ListLogger<PublishingTradeDecisionFailureReporter>();
        var handler = PriceHandler(DecisionService(new ThrowingSizing()), RuntimeReporter(host, logger));
        var handlerBus = new MessageContext(host.Services.GetRequiredService<IWolverineRuntime>());

        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
        {
            await Assert.ThrowsAsync<SecretBearingException>(() => handler.Handle(
                Trigger(), handlerBus, new Envelope { Attempts = WolverineExtensions.MaxDeliveryAttempts },
                CancellationToken.None));
        }));

        var failed = session.Sent.MessagesOf<TradeDecisionFailed>().Should().ContainSingle().Which;
        failed.CycleTrigger.Should().Be(BusinessMetrics.TriggerPriceMovement);
        failed.ExceptionType.Should().Be(typeof(SecretBearingException).FullName);
        session.Sent.Envelopes().Should().Contain(e =>
            e.Message is TradeDecisionFailed
            && e.Destination!.ToString() == "rabbitmq://exchange/AiStockTrading.Shared.Contracts.Events.TradeDecisionFailed");
        AssertNoSecret(JsonSerializer.Serialize(failed), "台帳へ渡る事実にメッセージ・スタックを載せない");
        AssertNoSecret(string.Join("\n", logger.Lines), "報告口のログにもメッセージ・スタックを載せない");
    }

    // ---- T-10-2181: 定時の経路は銘柄ごとに捕まえた失敗を 1 件ずつ渡し、他の銘柄の判断を続ける ----

    private static Task<IHost> StartScheduledHostAsync(ILlmCompletionClient llm, ITradeDecisionFailureReporter? reporter) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton<IMarketCalendar>(new OpenCalendar());
                opts.Services.AddSingleton<IWatchlistProvider>(new FakeWatchlist(
                    new WatchedSymbol("AAPL", Market.UnitedStates), new WatchedSymbol("MSFT", Market.UnitedStates)));
                opts.Services.AddSingleton(llm);
                opts.Services.AddSingleton<IDailyPolicyProvider, FakePolicy>();
                opts.Services.AddSingleton<ISizingContextProvider, FakeSizing>();
                opts.Services.AddScoped<AppSvc>();
                opts.Services.AddSingleton<BusinessMetrics>();
                opts.Services.AddSingleton<NewsCollectionStatusStore>();
                if (reporter is not null)
                    opts.Services.AddSingleton(reporter);
                else
                    // Program.cs と同じ登録（ランタイムの MessageBus から出す発行の実装）。
                    opts.Services.AddSingleton<ITradeDecisionFailureReporter>(sp => new PublishingTradeDecisionFailureReporter(
                        e => new MessageBus(sp.GetRequiredService<IWolverineRuntime>()).PublishAsync(e),
                        new FixedClock(), NullLogger<PublishingTradeDecisionFailureReporter>.Instance));

                opts.UseAiStockTradingRabbitMq(
                    ServiceName, "amqp://guest:guest@localhost:5672", typeof(InformationCollectedHandler).Assembly);
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    [Fact]
    public async Task T_10_2181_定時の銘柄ごとの失敗は1件だけ発生源つきで渡し_他の銘柄の判断を続ける()
    {
        var reporter = new RecordingTradeDecisionFailureReporter();
        using var host = await StartScheduledHostAsync(new ThrowingForSymbolLlm("AAPL"), reporter);

        var session = await host.TrackActivityForTest()
            .InvokeMessageAndWaitAsync(new InformationCollected(Guid.NewGuid(), 3, DateTimeOffset.UtcNow));

        var call = reporter.Calls.Should().ContainSingle().Which;
        call.CycleTrigger.Should().Be(BusinessMetrics.TriggerScheduled);
        call.Symbol.Should().Be("AAPL");
        call.Market.Should().Be(Market.UnitedStates);
        call.Exception.Should().BeOfType<SecretBearingException>();
        session.Sent.MessagesOf<TradeDecisionMade>().Should().ContainSingle()
            .Which.Intent.Symbol.Should().Be("MSFT", "1 銘柄の失敗で巡回を止めない（IADR-0023）");
        await host.StopAsync();
    }

    [Fact]
    public async Task T_10_2181_定時の判断がすべて成功すれば渡さない_否定形()
    {
        var reporter = new RecordingTradeDecisionFailureReporter();
        using var host = await StartScheduledHostAsync(new FakeLlm(BuyJson), reporter);

        await host.TrackActivityForTest()
            .InvokeMessageAndWaitAsync(new InformationCollected(Guid.NewGuid(), 3, DateTimeOffset.UtcNow));

        reporter.Calls.Should().BeEmpty();
        await host.StopAsync();
    }

    [Fact]
    public async Task T_10_2181_定時の失敗は本番と同じ発行の実装でTradeDecisionFailedとして1件外へ出る()
    {
        using var host = await StartScheduledHostAsync(new ThrowingForSymbolLlm("AAPL"), reporter: null);

        var session = await host.TrackActivityForTest()
            .InvokeMessageAndWaitAsync(new InformationCollected(Guid.NewGuid(), 3, DateTimeOffset.UtcNow));

        var failed = session.Sent.MessagesOf<TradeDecisionFailed>().Should().ContainSingle().Which;
        failed.Symbol.Should().Be("AAPL");
        failed.Market.Should().Be(Market.UnitedStates);
        failed.CycleTrigger.Should().Be(BusinessMetrics.TriggerScheduled);
        failed.ExceptionType.Should().Be(typeof(SecretBearingException).FullName);
        failed.OccurredAt.Should().Be(Now);
        AssertNoSecret(JsonSerializer.Serialize(failed), "台帳へ渡る事実にメッセージ・スタックを載せない");
        await host.StopAsync();
    }

    // ---- T-10-2182: 報告口は型名・発生源・銘柄・時刻だけを載せ、例外を投げない ----

    [Fact]
    public async Task T_10_2182_事実は型名と発生源と銘柄と時刻だけを持ちメッセージとスタックと秘密と口座IDを持たない_否定形()
    {
        var published = new List<TradeDecisionFailed>();
        var logger = new ListLogger<PublishingTradeDecisionFailureReporter>();
        var reporter = new PublishingTradeDecisionFailureReporter(
            e => { published.Add(e); return ValueTask.CompletedTask; }, new FixedClock(), logger);
        var exception = ThrownSecretException();
        exception.StackTrace.Should().NotBeNullOrEmpty("陰性の試験はスタックを持つ例外で行う");

        await reporter.ReportFinalFailureAsync(BusinessMetrics.TriggerScheduled, "NVDA", Market.UnitedStates, exception);

        var failed = published.Should().ContainSingle().Which;
        failed.EventId.Should().NotBe(Guid.Empty);
        failed.Symbol.Should().Be("NVDA");
        failed.Market.Should().Be(Market.UnitedStates);
        failed.CycleTrigger.Should().Be(BusinessMetrics.TriggerScheduled);
        failed.ExceptionType.Should().Be("TradeDecisionService.Tests.TradeDecisionFailureRecordTests+SecretBearingException");
        failed.OccurredAt.Should().Be(Now);

        // 事実の欄は 6 つだけ（メッセージ・スタック・内側の例外の欄を足さない）。
        typeof(TradeDecisionFailed).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
            [nameof(TradeDecisionFailed.EventId), nameof(TradeDecisionFailed.Symbol), nameof(TradeDecisionFailed.Market),
             nameof(TradeDecisionFailed.CycleTrigger), nameof(TradeDecisionFailed.ExceptionType), nameof(TradeDecisionFailed.OccurredAt)]);
        AssertNoSecret(JsonSerializer.Serialize(failed), "台帳へ渡る事実にメッセージ・スタックを載せない");
        AssertNoSecret(string.Join("\n", logger.Lines), "報告口のログにもメッセージ・スタックを載せない");
        string.Join("\n", logger.Lines).Should().Contain("SecretBearingException", "ログには型名を残す");
    }

    [Fact]
    public void T_10_2182_総称型の例外は定義の名前で型引数とアセンブリ名を含めない()
    {
        var name = PublishingTradeDecisionFailureReporter.ExceptionTypeName(new GenericFailure<int>(SecretMessage));

        name.Should().Be("TradeDecisionService.Tests.TradeDecisionFailureRecordTests+GenericFailure`1");
        name.Should().NotContain("Version=").And.NotContain("System.Int32").And.NotContain(AccountId);
    }

    public static TheoryData<string> PublishFailures => ["sync", "faulted", "canceled"];

    [Theory]
    [MemberData(nameof(PublishFailures))]
    public async Task T_10_2182_発行に失敗しても例外を投げず_型名だけを警告のログに残す(string mode)
    {
        var logger = new ListLogger<PublishingTradeDecisionFailureReporter>();
        var reporter = new PublishingTradeDecisionFailureReporter(
            _ => mode switch
            {
                "sync" => throw new IOException("broker down"),
                "faulted" => ValueTask.FromException(new TimeoutException("broker slow")),
                _ => ValueTask.FromCanceled(new CancellationToken(canceled: true)),
            },
            new FixedClock(), logger);

        var act = () => reporter.ReportFinalFailureAsync(
            BusinessMetrics.TriggerPriceMovement, "AAPL", Market.UnitedStates, ThrownSecretException());

        await act.Should().NotThrowAsync("報告の失敗で呼び出し元の挙動（次の銘柄へ進む・投げ直す）を変えない");
        var log = string.Join("\n", logger.Lines);
        log.Should().Contain("Warning: ").And.Contain("発行できませんでした").And.Contain("SecretBearingException");
        AssertNoSecret(log, "発行の失敗のログにも元の例外の本文を載せない");
    }
}
