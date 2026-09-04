using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.TestSupport.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.MarketData;

// FR-01, ADR-0031（計画）決定2〜4, IADR-0292: 実市況 4 サービスぶんの Finnhub 日次要求量の見積り配線
// （警告ログ・業務メトリクス）。既定（EstimatedSymbolCount=0 ＝未申告）は挙動中立であることを固定する。
public class MarketDataSourceFactoryDailyVolumeTests
{
    [Fact]
    public void 銘柄数未申告_既定0_は見積らず警告もメトリクスも出さない()
    {
        // #695: 否定形の表明なので Meter をこのテストへ隔離する（既定名だと並行する
        // 別テストの測定値を拾い、BeEmpty が他人の発火で偽陽性になる）。
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = new BusinessMetrics(meterName);
        var logs = new CapturingLoggerFactory();

        MarketDataSourceFactory.EvaluateDailyVolume(
            new MarketDataOptions(), pollIntervalSeconds: 60, new FinnhubDailyVolumeGuardOptions(), metrics, logs);

        logs.Warnings.Should().BeEmpty();
        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate).Should().BeEmpty();
    }

    [Fact]
    public void 暫定上限内の申告銘柄数は警告を出さないがメトリクスは記録する()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var metrics = new BusinessMetrics();
        var logs = new CapturingLoggerFactory();
        // 60 秒間隔 × 1 銘柄 × 1440 巡回/日 = 1440 件……上限内に収めるため巡回間隔を長くする。
        var options = new MarketDataOptions { Finnhub = new FinnhubMarketDataOptions { EstimatedSymbolCount = 1 } };

        // 1 日 1 巡回（間隔 86400 秒）× 1 銘柄 = 1 件/日。
        MarketDataSourceFactory.EvaluateDailyVolume(
            options, pollIntervalSeconds: 86400, new FinnhubDailyVolumeGuardOptions(), metrics, logs);

        logs.Warnings.Should().BeEmpty();
        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate).Should().ContainSingle(m => m.Value == 1);
    }

    [Fact]
    public void 暫定上限を超える申告銘柄数は警告を出しメトリクスへ超過比率を記録する()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var metrics = new BusinessMetrics();
        var logs = new CapturingLoggerFactory();
        // 60 秒間隔（MarketMonitorService 既定）× 1 銘柄 × 1440 巡回/日 = 1440 件/日 > 300。
        var options = new MarketDataOptions { Finnhub = new FinnhubMarketDataOptions { EstimatedSymbolCount = 1 } };

        MarketDataSourceFactory.EvaluateDailyVolume(
            options, pollIntervalSeconds: 60, new FinnhubDailyVolumeGuardOptions(), metrics, logs);

        logs.Warnings.Should().ContainSingle(m =>
            m.Contains("1440", StringComparison.Ordinal) && m.Contains("300", StringComparison.Ordinal));
        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate).Should().ContainSingle(m => m.Value == 1440);
        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeLimitRatioPercent)
            .Should().ContainSingle(m => m.Value > 100);
    }

    [Fact]
    public void 送出は停止しない_送出そのものは検証対象外だが例外を投げないことで示す()
    {
        // ADR-0031 決定3: 超過は警告に留め、統制としては可視化のみ（強制停止しない）。
        var act = () => MarketDataSourceFactory.EvaluateDailyVolume(
            new MarketDataOptions { Finnhub = new FinnhubMarketDataOptions { EstimatedSymbolCount = 100 } },
            pollIntervalSeconds: 1,
            new FinnhubDailyVolumeGuardOptions(),
            new BusinessMetrics(),
            new CapturingLoggerFactory());

        act.Should().NotThrow();
    }

    [Fact]
    public void EstimateDailyVolumeは未申告なら0を返す()
    {
        MarketDataSourceFactory.EstimateDailyVolume(new MarketDataOptions(), 60).Should().Be(0);
    }

    [Fact]
    public void EstimateDailyVolumeは申告銘柄数と巡回間隔から算出する()
    {
        var options = new MarketDataOptions { Finnhub = new FinnhubMarketDataOptions { EstimatedSymbolCount = 6 } };

        MarketDataSourceFactory.EstimateDailyVolume(options, pollIntervalSeconds: 1800).Should().Be(6 * 48);
    }

    // 構成不備・超過の警告は「有効化したつもりで効いていない／統制の可視化」の唯一の検知点であるため、
    // 出力そのものを検証する。中央パッケージ管理にログ用の偽装は無いので最小の実装を置く（FxRateSourceFactoryTests と同型）。
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
