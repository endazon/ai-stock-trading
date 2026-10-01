using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.TestSupport.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.MarketData;

// FR-01, ADR-0031（計画）決定2〜4, ADR-0043（計画）決定 1・3, #1132, IADR-0477: Finnhub の日次要求見積りは運用者の申告ではなく、
// 巡回の対象の銘柄の実数から数える。是正前は申告 1 銘柄で数え、実測と桁で外れた（市場監視 390 に対し実測 ≈ 3,510）。
public class FinnhubDailyVolumeRecorderTests
{
    // 共有カーネルの MarketSessions.RegularSessionMinutes と同じ値（本プロジェクトはカーネルを参照しないため写す）。
    private static int Session(Market market) => market == Market.UnitedStates ? 390 : 300;

    private static readonly MarketDataOptions FinnhubWithKey = new()
    {
        Provider = "finnhub",
        Finnhub = new FinnhubMarketDataOptions { ApiKey = "test-key" },
    };

    private static Market[] Us(int count) => Enumerable.Repeat(Market.UnitedStates, count).ToArray();

    // 🔴 T-10-2010: 1 巡回の米国の銘柄 9 件・60 秒巡回なら 9 × 390 ＝ 3,510（#1132 の実測と一致）。東証の銘柄は 0、
    // 120 秒巡回なら半分、空なら 0。申告の 1 銘柄（390）には戻らない。
    [Fact]
    public void 見積りは巡回の対象の米国の銘柄数と場中の巡回回数の積()
    {
        FinnhubDailyVolumeEstimator.EstimateForSymbols(Us(9), 60, Session).Should().Be(3_510);
        FinnhubDailyVolumeEstimator.EstimateForSymbols([.. Us(3), Market.Japan, Market.Japan], 60, Session)
            .Should().Be(1_170, "東証の銘柄は Finnhub へ送らない");
        FinnhubDailyVolumeEstimator.EstimateForSymbols(Us(9), 120, Session).Should().Be(9 * 195);
        FinnhubDailyVolumeEstimator.EstimateForSymbols([], 60, Session).Should().Be(0);
        FinnhubDailyVolumeEstimator.EstimateForSymbols(Us(1), 0, Session).Should().Be(390 * 60, "1 秒未満は 1 秒として数える");
        FinnhubDailyVolumeEstimator.RequestsPerSymbol(Market.UnitedStates).Should().Be(1);
        FinnhubDailyVolumeEstimator.RequestsPerSymbol(Market.Japan).Should().Be(0);
    }

    // T-10-2011: Finnhub へ送らない構成（Provider が finnhub 以外・鍵が無い）では記録しない（挙動中立）。
    [Theory]
    [InlineData("", "test-key")]
    [InlineData("none", "test-key")]
    [InlineData("finnhub", "")]
    [InlineData("finnhub", null)]
    public void Finnhubへ送らない構成では記録しない(string provider, string? apiKey)
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        var logs = new CapturingLogger();
        var options = new MarketDataOptions { Provider = provider, Finnhub = new FinnhubMarketDataOptions { ApiKey = apiKey } };

        var recorder = new FinnhubDailyVolumeRecorder(options, new FinnhubDailyVolumeGuardOptions(), metrics, logs, Session);

        recorder.Record(Us(9), 60).Should().BeNull();
        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate).Should().BeEmpty();
        logs.Entries.Should().BeEmpty();
    }

    // 🔴 T-10-2012: 上限が未設定（既定）なら見積りだけを毎回記録し、比率は記録せず警告も出さない。情報ログは値が変わったときだけ。
    [Fact]
    public void 既定では見積りを毎回記録しログは値が変わったときだけ出す()
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        var logs = new CapturingLogger();
        var recorder = new FinnhubDailyVolumeRecorder(FinnhubWithKey, new FinnhubDailyVolumeGuardOptions(), metrics, logs, Session);

        recorder.Record(Us(9), 60).Should().Be(3_510);
        recorder.Record(Us(9), 60).Should().Be(3_510);
        recorder.Record(Us(10), 60).Should().Be(3_900);

        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate).Select(m => m.Value)
            .Should().Equal(3_510, 3_510, 3_900);
        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeLimitRatioPercent).Should().BeEmpty("推測の分母で割った比率を出さない");
        logs.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        logs.Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message)
            .Should().SatisfyRespectively(
                first => first.Should().Contain("3510").And.Contain("9 件"),
                second => second.Should().Contain("3900").And.Contain("10 件"));
    }

    // T-10-2013: 上限を設定して超えたら警告と比率（100 超）。上限以内なら比率だけ（警告なし）。送出は止めない（例外を投げない）。
    [Fact]
    public void 上限を設定したときだけ超過を警告し比率を記録する()
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        var logs = new CapturingLogger();
        var recorder = new FinnhubDailyVolumeRecorder(
            FinnhubWithKey, new FinnhubDailyVolumeGuardOptions { ProvisionalDailyLimit = 3_510 }, metrics, logs, Session);

        recorder.Record(Us(9), 60).Should().Be(3_510);  // ちょうど上限＝以内
        recorder.Record(Us(10), 60).Should().Be(3_900); // 超過

        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeLimitRatioPercent).Select(m => m.Value)
            .Should().Equal(100d, 3_900d / 3_510 * 100);
        logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("3900").And.Contain("3510");
    }

    // T-10-2014: Finnhub へ送るかの判定は Create と同じ（Provider は前後の空白と大小を問わない・鍵が要る）。
    [Fact]
    public void Finnhubへ送るかはProviderと鍵で決まる()
    {
        MarketDataSourceFactory.SendsToFinnhub(FinnhubWithKey).Should().BeTrue();
        MarketDataSourceFactory.SendsToFinnhub(new MarketDataOptions
        {
            Provider = " FinnHub ",
            Finnhub = new FinnhubMarketDataOptions { ApiKey = "k" },
        }).Should().BeTrue();
        MarketDataSourceFactory.SendsToFinnhub(new MarketDataOptions { Provider = "finnhub" }).Should().BeFalse();
        MarketDataSourceFactory.SendsToFinnhub(new MarketDataOptions
        {
            Provider = "none",
            Finnhub = new FinnhubMarketDataOptions { ApiKey = "k" },
        }).Should().BeFalse();
        MarketDataSourceFactory.SendsToFinnhub(new MarketDataOptions()).Should().BeFalse();
    }

    // 🔴 T-10-2018: 運用者の申告（旧 EstimatedSymbolCount）と、申告で数える起動時の見積り（旧 EstimateDailyVolume /
    // EvaluateDailyVolume）は撤去した。戻すと申告 1 で数えた値が再び実数の記録と同じ系列へ混ざる。
    [Fact]
    public void 申告銘柄数による見積りの入口は無い()
    {
        typeof(FinnhubMarketDataOptions).GetProperty("EstimatedSymbolCount").Should().BeNull();
        typeof(MarketDataSourceFactory).GetMethod("EstimateDailyVolume").Should().BeNull();
        typeof(MarketDataSourceFactory).GetMethod("EvaluateDailyVolume").Should().BeNull();
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
