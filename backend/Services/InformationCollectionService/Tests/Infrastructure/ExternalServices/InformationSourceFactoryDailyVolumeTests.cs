using InformationCollectionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.TestSupport.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InformationCollectionService.Tests;

// FR-01, ADR-0031（計画）決定2〜4, IADR-0292: 情報収集ぶんの Finnhub 日次要求見積りの配線
// （警告ログ・業務メトリクス）。銘柄未設定・Finnhub 系ソース未有効なら挙動中立であることを固定する。
public class InformationSourceFactoryDailyVolumeTests
{
    [Fact]
    public void 銘柄未設定は見積らず警告もメトリクスも出さない()
    {
        // #695: 否定形の表明なので Meter をこのテストへ隔離する（既定名だと並行する
        // 別テストの測定値を拾い、BeEmpty が他人の発火で偽陽性になる）。
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        var logs = new CapturingLoggerFactory();

        InformationSourceFactory.EvaluateDailyVolumeEstimate(
            new CollectionSourceOptions { Provider = "finnhub" },
            pollIntervalSeconds: 1800,
            new FinnhubDailyVolumeGuardOptions(),
            metrics,
            logs);

        logs.Warnings.Should().BeEmpty();
        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate).Should().BeEmpty();
    }

    [Fact]
    public void finnhub系ソースが未有効なら銘柄設定があっても見積らない()
    {
        // #695: 否定形の表明なので Meter をこのテストへ隔離する（既定名だと並行する
        // 別テストの測定値を拾い、BeEmpty が他人の発火で偽陽性になる）。
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        var logs = new CapturingLoggerFactory();

        InformationSourceFactory.EvaluateDailyVolumeEstimate(
            new CollectionSourceOptions
            {
                Provider = "sec-edgar",
                Finnhub = new FinnhubOptions { Symbols = ["AAPL"] },
            },
            pollIntervalSeconds: 1800,
            new FinnhubDailyVolumeGuardOptions(),
            metrics,
            logs);

        logs.Warnings.Should().BeEmpty();
        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate).Should().BeEmpty();
    }

    [Fact]
    public void 暫定上限内の銘柄数は警告を出さないがメトリクスは記録する()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var metrics = new BusinessMetrics();
        var logs = new CapturingLoggerFactory();
        // finnhub のみ有効（1 要求/巡回）× 1 銘柄 × 48 巡回/日（30 分間隔） = 48 件/日。
        var options = new CollectionSourceOptions
        {
            Provider = "finnhub",
            Finnhub = new FinnhubOptions { Symbols = ["AAPL"] },
        };

        InformationSourceFactory.EvaluateDailyVolumeEstimate(
            options, pollIntervalSeconds: 1800, new FinnhubDailyVolumeGuardOptions(), metrics, logs);

        logs.Warnings.Should().BeEmpty();
        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate).Should().ContainSingle(m => m.Value == 48);
    }

    [Fact]
    public void finnhub_newsも有効なら1銘柄2要求で見積る()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var metrics = new BusinessMetrics();
        var logs = new CapturingLoggerFactory();
        var options = new CollectionSourceOptions
        {
            Provider = "finnhub,finnhub-news",
            Finnhub = new FinnhubOptions { Symbols = ["AAPL"] },
        };

        InformationSourceFactory.EvaluateDailyVolumeEstimate(
            options, pollIntervalSeconds: 1800, new FinnhubDailyVolumeGuardOptions(), metrics, logs);

        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate).Should().ContainSingle(m => m.Value == 96);
    }

    [Fact]
    public void 暫定上限を超える銘柄数は警告を出しメトリクスへ超過比率を記録する()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var metrics = new BusinessMetrics();
        var logs = new CapturingLoggerFactory();
        // finnhub 1 要求/巡回 × 10 銘柄 × 48 巡回/日 = 480 件/日 > 300。
        var options = new CollectionSourceOptions
        {
            Provider = "finnhub",
            Finnhub = new FinnhubOptions { Symbols = [.. Enumerable.Range(0, 10).Select(i => $"SYM{i}")] },
        };

        InformationSourceFactory.EvaluateDailyVolumeEstimate(
            options, pollIntervalSeconds: 1800, new FinnhubDailyVolumeGuardOptions(), metrics, logs);

        logs.Warnings.Should().ContainSingle(m =>
            m.Contains("480", StringComparison.Ordinal) && m.Contains("300", StringComparison.Ordinal));
        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeLimitRatioPercent)
            .Should().ContainSingle(m => m.Value > 100);
    }

    [Fact]
    public void EstimateDailyVolumeは銘柄未設定なら0を返す()
    {
        InformationSourceFactory.EstimateDailyVolume(
            new CollectionSourceOptions { Provider = "finnhub" }, pollIntervalSeconds: 1800).Should().Be(0);
    }

    // 構成不備・超過の警告は「有効化したつもりで効いていない／統制の可視化」の唯一の検知点であるため、
    // 出力そのものを検証する（FxRateSourceFactoryTests と同型）。
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings => _warnings;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_warnings);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                    warnings.Add(formatter(state, exception));
            }
        }
    }
}
