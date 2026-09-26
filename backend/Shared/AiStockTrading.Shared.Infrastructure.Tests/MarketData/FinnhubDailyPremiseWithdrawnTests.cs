using System.Net;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using AiStockTrading.TestSupport.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.MarketData;

// FR-01, FR-03, ADR-0043（計画）決定 1・3, #1030, IADR-0437:
// - 決定 1: 暫定の 300 回/日を撤回し、既定では何とも比べない。日次は分次の窓で説明できない 429 で見張る。
// - 決定 3: 1 日の巡回回数は開場中の巡回だけで数える（閉場中は巡回しないプロセス）。
public class FinnhubDailyPremiseWithdrawnTests
{
    // T-10-1431: 巡回する時間（分）を渡すと、その時間だけで巡回回数を数える。既定（24 時間）は従来どおり。
    [Fact]
    public void 巡回回数は巡回する時間だけで数える()
    {
        FinnhubDailyVolumeEstimator.CyclesPerDay(60, activeMinutesPerDay: 390).Should().Be(390, "米国の立会 390 分 ÷ 60 秒");
        FinnhubDailyVolumeEstimator.CyclesPerDay(120, activeMinutesPerDay: 390).Should().Be(195);
        FinnhubDailyVolumeEstimator.CyclesPerDay(7, activeMinutesPerDay: 1).Should().Be(8, "切り捨て（60 ÷ 7）");
        FinnhubDailyVolumeEstimator.CyclesPerDay(60).Should().Be(1440, "既定は 24 時間（開場に関係なく巡回するプロセス）");
        FinnhubDailyVolumeEstimator.CyclesPerDay(60, activeMinutesPerDay: 0).Should().Be(0);

        var negative = () => FinnhubDailyVolumeEstimator.CyclesPerDay(60, activeMinutesPerDay: -1);
        var overDay = () => FinnhubDailyVolumeEstimator.CyclesPerDay(60, activeMinutesPerDay: 1441);
        negative.Should().Throw<ArgumentOutOfRangeException>();
        overDay.Should().Throw<ArgumentOutOfRangeException>();
    }

    // T-10-1433: 日次上限が無ければ比べない（NotCompared・比率なし）。上限を設定したときだけ従来どおり比べる。
    [Fact]
    public void 日次上限が無ければ比べない()
    {
        var none = FinnhubDailyVolumeEstimator.Evaluate(100_000, provisionalDailyLimit: null);
        none.Verdict.Should().Be(FinnhubDailyVolumeEstimator.Verdict.NotCompared);
        none.ExceedRatio.Should().BeNull();
        none.ProvisionalDailyLimit.Should().BeNull();

        FinnhubDailyVolumeEstimator.Evaluate(301, provisionalDailyLimit: 300).Verdict
            .Should().Be(FinnhubDailyVolumeEstimator.Verdict.Exceeds);
        FinnhubDailyVolumeEstimator.Evaluate(300, provisionalDailyLimit: 300).Verdict
            .Should().Be(FinnhubDailyVolumeEstimator.Verdict.Within);
        FinnhubDailyVolumeEstimator.Evaluate(
            [new FinnhubDailyVolumeEstimator.ProcessVolume("a", "k", 10, 1, 1440)], provisionalDailyLimit: null)[0].Verdict
            .Should().Be(FinnhubDailyVolumeEstimator.Verdict.NotCompared);
        new FinnhubDailyVolumeGuardOptions().ProvisionalDailyLimit.Should().BeNull("既定は 300 ではない（撤回）");
    }

    // T-10-1434: 既定（上限なし）では警告を出さず、比率も記録しない。見積りは記録し、開場中の巡回で数えられる。
    [Fact]
    public void 既定では警告も比率も出さず見積りだけを開場中の巡回で記録する()
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        var logs = new CapturingLoggerFactory();
        // 監視 6 銘柄 × 1 要求 × 390 巡回（米国 390 分 ÷ 60 秒）＝ 2,340。24 時間なら 8,640。
        var options = new MarketDataOptions { Finnhub = new FinnhubMarketDataOptions { EstimatedSymbolCount = 6 } };

        MarketDataSourceFactory.EvaluateDailyVolume(
            options, pollIntervalSeconds: 60, new FinnhubDailyVolumeGuardOptions(), metrics, logs, activeMinutesPerDay: 390);

        logs.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Message.Contains("2340", StringComparison.Ordinal));
        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate).Should().ContainSingle(m => m.Value == 2340);
        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeLimitRatioPercent).Should().BeEmpty("推測の分母で割った比率を出さない");
        MarketDataSourceFactory.EstimateDailyVolume(options, 60, activeMinutesPerDay: 390).Should().Be(2340);
        MarketDataSourceFactory.EstimateDailyVolume(options, 60).Should().Be(8640, "既定は 24 時間のまま");
    }

    // T-10-1446: 残りがあるのに拒否された 429 は分次では説明できない（日次の手がかり）。
    [Theory]
    [InlineData(1)]
    [InlineData(59)]
    public void 残りがあるのに拒否された429は日次の手がかり(int remaining)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        var kind = FinnhubRateLimitClassifier.Classify(remaining, now.AddSeconds(30), null, now);

        kind.Should().Be(FinnhubRateLimitClassifier.Kind.RemainingNotExhausted);
        FinnhubRateLimitClassifier.IsDailyLimitClue(kind).Should().BeTrue();

        // #1037 の監査: 直前の要求から 1 秒以内なら秒次（30 回/秒）の 429 の余地がある＝手がかりにしない。ちょうど 1 秒からは手がかり。
        var burst = FinnhubRateLimitClassifier.Classify(remaining, now.AddSeconds(30), null, now, TimeSpan.FromMilliseconds(999));
        burst.Should().Be(FinnhubRateLimitClassifier.Kind.PossibleBurst);
        FinnhubRateLimitClassifier.IsDailyLimitClue(burst).Should().BeFalse();
        FinnhubRateLimitClassifier.Classify(remaining, now.AddSeconds(30), null, now, TimeSpan.FromSeconds(1))
            .Should().Be(FinnhubRateLimitClassifier.Kind.RemainingNotExhausted);
    }

    // T-10-1447: リセットの時刻を過ぎても続く拒否は分次では説明できない（その応答のリセットが過去／前回の 429 のリセットを過ぎた）。
    // 猶予（2 秒）の内側は鳴らさない（時計のずれで誤って鳴らさない）。
    [Fact]
    public void リセットの後も続く429は日次の手がかり()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        FinnhubRateLimitClassifier.Classify(0, now.AddSeconds(-3), null, now)
            .Should().Be(FinnhubRateLimitClassifier.Kind.PersistsAfterReset, "この応答のリセットが既に過去");
        FinnhubRateLimitClassifier.Classify(0, null, previousRejectionReset: now.AddSeconds(-3), now)
            .Should().Be(FinnhubRateLimitClassifier.Kind.PersistsAfterReset, "この応答にリセットが無く、前回の 429 のリセットを過ぎて成功を挟まずにまた 429");
        FinnhubRateLimitClassifier.Classify(null, null, previousRejectionReset: now.AddSeconds(-3), now)
            .Should().Be(FinnhubRateLimitClassifier.Kind.PersistsAfterReset, "ヘッダが無くても前回のリセットを過ぎていれば");
        FinnhubRateLimitClassifier.Classify(0, now.AddSeconds(-1), null, now)
            .Should().Be(FinnhubRateLimitClassifier.Kind.MinuteWindow, "猶予の内側");
        FinnhubRateLimitClassifier.Classify(0, null, previousRejectionReset: now.AddSeconds(-1), now)
            .Should().Be(FinnhubRateLimitClassifier.Kind.MinuteWindow, "猶予の内側");
    }

    // T-10-1448: 残り 0・リセットは未来の 429 は分次の窓で説明できる（日次の手がかりにしない）。ヘッダが無ければ判別できない。
    [Fact]
    public void 分次の窓で説明できる429は手がかりにしない()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        var window = FinnhubRateLimitClassifier.Classify(0, now.AddSeconds(20), previousRejectionReset: now.AddSeconds(20), now);
        window.Should().Be(FinnhubRateLimitClassifier.Kind.MinuteWindow);
        FinnhubRateLimitClassifier.IsDailyLimitClue(window).Should().BeFalse();

        // #1037 の監査: この応答が「残り 0・リセットは未来」なら、前回の 429 のリセットを過ぎていても新しい窓を使い切った 429（分次で説明できる）。
        FinnhubRateLimitClassifier.Classify(0, now.AddSeconds(20), previousRejectionReset: now.AddSeconds(-40), now)
            .Should().Be(FinnhubRateLimitClassifier.Kind.MinuteWindow);

        var missing = FinnhubRateLimitClassifier.Classify(null, null, null, now);
        missing.Should().Be(FinnhubRateLimitClassifier.Kind.HeadersMissing);
        FinnhubRateLimitClassifier.IsDailyLimitClue(missing).Should().BeFalse();
    }

    // T-10-1449: クライアントは分次で説明できない 429 を専用のイベントで記録し、分次の 429 は従来の警告のまま。
    // 成功を挟むと前回の 429 の記憶を消す（次の 429 は新しい窓で判定する）。取得はいずれも null（スキップ）で送出は変えない。
    [Fact]
    public async Task クライアントは分次で説明できない429を専用のイベントで記録する()
    {
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var handler = new ScriptedHandler();
        var logs = new CapturingLoggerFactory();
        var client = new FinnhubQuoteClient(
            new HttpClient(handler), "key", new NoWaitLimiter(), logs.CreateLogger("finnhub"), timeProvider: clock);
        var reset = clock.GetUtcNow().AddSeconds(30).ToUnixTimeSeconds();

        // 1) 分次の窓（残り 0・リセットは未来）→ 従来の警告。
        handler.Next = Rejected(remaining: 0, reset);
        (await client.GetQuoteAsync("AAPL")).Should().BeNull();
        logs.Entries.Should().ContainSingle().Which.EventId.Should().NotBe(FinnhubQuoteClient.DailyLimitClueEvent);

        // 2a) リセットを過ぎたが、この応答は「残り 0・リセットは未来」＝新しい窓を使い切った 429（分次で説明できる。#1037 の監査）。
        clock.Advance(TimeSpan.FromSeconds(35));
        handler.Next = Rejected(remaining: 0, clock.GetUtcNow().AddSeconds(25).ToUnixTimeSeconds());
        (await client.GetQuoteAsync("AAPL")).Should().BeNull();
        logs.Entries[^1].EventId.Should().NotBe(FinnhubQuoteClient.DailyLimitClueEvent);

        // 2b) そのリセットも過ぎ、成功を挟まずにヘッダの無い 429 → 前回のリセットで判定して日次の手がかり。
        clock.Advance(TimeSpan.FromSeconds(30));
        handler.Next = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
        (await client.GetQuoteAsync("AAPL")).Should().BeNull();
        logs.Entries[^1].EventId.Should().Be(FinnhubQuoteClient.DailyLimitClueEvent);
        logs.Entries[^1].Message.Should().Contain("分次の窓では説明できません").And.Contain("AAPL");

        // 3) 成功を挟むと記憶が消え、次の窓内の 429 は分次の窓として扱う。
        handler.Next = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"c":1,"h":1,"l":1,"pc":1,"t":1800000000}"""),
        };
        (await client.GetQuoteAsync("AAPL")).Should().NotBeNull();
        clock.Advance(TimeSpan.FromSeconds(60));
        handler.Next = Rejected(remaining: 0, clock.GetUtcNow().AddSeconds(30).ToUnixTimeSeconds());
        (await client.GetQuoteAsync("AAPL")).Should().BeNull();
        logs.Entries[^1].EventId.Should().NotBe(FinnhubQuoteClient.DailyLimitClueEvent);

        // 4) 直前の要求から 2 秒後に、残りがあるのに 429 → 日次の手がかり。
        clock.Advance(TimeSpan.FromSeconds(2));
        handler.Next = Rejected(remaining: 12, clock.GetUtcNow().AddSeconds(30).ToUnixTimeSeconds());
        (await client.GetQuoteAsync("MSFT")).Should().BeNull();
        logs.Entries[^1].EventId.Should().Be(FinnhubQuoteClient.DailyLimitClueEvent);

        // 5) 直前の要求から 1 秒以内に、残りがあるのに 429 → 秒次（30 回/秒）の余地があるので手がかりにしない（#1037 の監査）。
        clock.Advance(TimeSpan.FromMilliseconds(200));
        handler.Next = Rejected(remaining: 11, clock.GetUtcNow().AddSeconds(30).ToUnixTimeSeconds());
        (await client.GetQuoteAsync("NVDA")).Should().BeNull();
        logs.Entries[^1].EventId.Should().NotBe(FinnhubQuoteClient.DailyLimitClueEvent);
        logs.Entries[^1].Message.Should().Contain("PossibleBurst");
        logs.Entries.Count(e => e.EventId == FinnhubQuoteClient.DailyLimitClueEvent).Should().Be(2);
    }

    // T-10-1573（#1044 項目 3, IADR-0437 決定 7）: 追跡器を共有すると、クライアントを通らない同じ鍵の送り手（情報収集の企業ニュース）の
    // 送出も「直前の要求」に数える。その直後 1 秒以内の「残りがあるのに拒否」は秒次の余地（4301 にしない）。追跡器を共有しない
    // クライアントは同じ並びで 4301 を記録する（是正前の挙動＝陽性対照）。
    [Fact]
    public async Task 共有の追跡器は他の送り手の要求も直前の要求に数える()
    {
        async Task<CapturingLoggerFactory> Run(bool share)
        {
            var clock = new ManualClock(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
            var handler = new ScriptedHandler();
            var logs = new CapturingLoggerFactory();
            var tracker = new FinnhubLastRequestTracker(clock);
            var client = new FinnhubQuoteClient(
                new HttpClient(handler), "key", new NoWaitLimiter(), logs.CreateLogger("finnhub"), timeProvider: clock,
                lastRequestTracker: share ? tracker : null);

            handler.Next = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"c":1,"h":1,"l":1,"pc":1,"t":1800000000}"""),
            };
            (await client.GetQuoteAsync("AAPL")).Should().NotBeNull();

            clock.Advance(TimeSpan.FromSeconds(5));
            tracker.MarkSent().SincePrevious.Should().Be(share ? TimeSpan.FromSeconds(5) : null, "他の送り手（企業ニュース）の送出");
            clock.Advance(TimeSpan.FromMilliseconds(200));
            handler.Next = Rejected(remaining: 7, clock.GetUtcNow().AddSeconds(30).ToUnixTimeSeconds());
            (await client.GetQuoteAsync("MSFT")).Should().BeNull();
            return logs;
        }

        var shared = await Run(share: true);
        shared.Entries.Should().NotContain(e => e.EventId == FinnhubQuoteClient.DailyLimitClueEvent);
        shared.Entries[^1].Message.Should().Contain("PossibleBurst");

        var own = await Run(share: false);
        own.Entries[^1].EventId.Should().Be(FinnhubQuoteClient.DailyLimitClueEvent, "自分の送出だけを数えると直前は 5.2 秒前");
    }

    private static HttpResponseMessage Rejected(int remaining, long resetUnix)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
        response.Headers.Add(FinnhubRateLimitClassifier.RemainingHeader, remaining.ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.Add(FinnhubRateLimitClassifier.ResetHeader, resetUnix.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public HttpResponseMessage? Next { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Next ?? throw new InvalidOperationException("応答が用意されていません"));
    }

    private sealed class NoWaitLimiter : IRateLimiter
    {
        public Task WaitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // FakeTimeProvider は中央パッケージ管理に未登録のため最小の偽装を置く（MarketDataSourceTests と同じ理由）。
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message);

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<LogEntry> _entries = [];

        public List<LogEntry> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception)));
        }
    }
}
