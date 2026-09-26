using System.Net;
using System.Text;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.TestSupport.Metrics;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AwesomeAssertions;
using InformationCollectionService.Common.Abstractions;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine;
using Xunit;

namespace InformationCollectionService.Tests;

// FR-01, FR-13, ADR-0043（計画）決定 1〜3, #1030, IADR-0437（T-10-1452〜T-10-1455・T-10-1457）: 情報収集の Finnhub の日次要求の見積りは、
// 監視銘柄に追随する構成（#1015 / IADR-0435）では構成の固定リストではなく、起動時は 1 巡回の上限、巡回ごとはその巡回の対象の数で数える。
// 日次上限は未実測が平常（比べない・警告しない）。1 銘柄あたりの要求数と、市場監視の照会の短いタイムアウトを固定する。
public class FinnhubDailyEstimateFollowsWatchlistTests
{
    // T-10-1452: 銘柄数を与えればその数で数え、省略時は構成の固定リストの数。24 時間の巡回で数える（in-process の巡回は開場に関係なく回る）。
    // 日次上限が未設定なら情報ログだけで警告しない。
    [Fact]
    public void 見積りは与えた対象の数で数え上限が無ければ警告しない()
    {
        var options = new CollectionSourceOptions
        {
            Provider = "finnhub,finnhub-news",
            Finnhub = new FinnhubOptions { Symbols = ["AAPL"] },
        };

        InformationSourceFactory.EstimateDailyVolume(options, 1800).Should().Be(1 * 2 * 48, "省略時は構成の固定リスト");
        InformationSourceFactory.EstimateDailyVolume(options, 1800, symbolCount: 6).Should().Be(6 * 2 * 48);
        InformationSourceFactory.EstimateDailyVolume(options, 1800, symbolCount: 0).Should().Be(0);
        InformationSourceFactory.EstimateDailyVolume(new CollectionSourceOptions { Provider = "fred" }, 1800, symbolCount: 6)
            .Should().Be(0, "Finnhub 系が無効なら数えない");
        // 30 回/分 × 300 秒 ÷ 2 要求 ＝ 75 銘柄（選択器と同じ式）。
        InformationSourceFactory.FinnhubMaxSymbolsPerCycle(options, 300).Should().Be(75);

        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        var logs = new CapturingLoggerFactory();
        InformationSourceFactory.EvaluateDailyVolumeEstimate(
            options, 1800, new FinnhubDailyVolumeGuardOptions(), metrics, logs, symbolCount: 6);

        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate).Should().ContainSingle(m => m.Value == 576);
        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeLimitRatioPercent).Should().BeEmpty();
        logs.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Message.Contains("576", StringComparison.Ordinal)
            && e.Message.Contains("銘柄数 6", StringComparison.Ordinal));
    }

    // T-10-1453: 1 銘柄あたりの要求数は Provider に列挙された finnhub・finnhub-news の数（大小文字・空白・重複・none を吸収）。
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("none", 0)]
    [InlineData("sec-edgar,fred", 0)]
    [InlineData("finnhub", 1)]
    [InlineData("finnhub-news", 1)]
    [InlineData("finnhub,finnhub-news", 2)]
    [InlineData(" FINNHUB , Finnhub-News ,fred", 2)]
    [InlineData("finnhub,finnhub", 1)]
    public void 一銘柄あたりの要求数はfinnhubとfinnhub_newsの数(string? provider, int expected)
    {
        InformationSourceFactory.FinnhubRequestsPerSymbol(new CollectionSourceOptions { Provider = provider }).Should().Be(expected);
    }

    // T-10-1454: 本番の組み立てで、監視銘柄に追随するなら、起動時の見積りは 1 巡回の上限で数え、巡回ごとにその巡回の対象の数で記録し直す
    // （構成の固定リスト〔ここでは 1 件〕では数えない）。
    [Fact]
    public async Task 追随する構成の見積りは起動時は上限で巡回ごとは対象の数で記録する()
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        using var factory = new Factory(
            new Dictionary<string, string?>
            {
                ["MarketMonitor:BaseUrl"] = "http://market-monitor-service:8080",
                ["Collection:Source:Provider"] = "finnhub",
                ["Collection:Source:Finnhub:ApiKey"] = "key",
                ["Collection:Source:Finnhub:Symbols:0"] = "ZZZZ",
            },
            metrics);
        _ = factory.CreateClient();

        var fetcher = factory.Services.GetRequiredService<ISourceFetcher>();
        // 30 回/分 × 3600 秒 ÷ 1 要求 ＝ 1,800 銘柄が上限。1 日 24 巡回（3600 秒）。
        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate).Should().ContainSingle(m => m.Value == 1800 * 24);

        await fetcher.FetchAllAsync();

        // 監視銘柄の米国の銘柄は AAPL・MSFT の 2 件 → 2 × 1 × 24。
        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate).Select(m => m.Value).Should().Equal(1800 * 24, 2 * 24);
    }

    // T-10-1455: 市場監視の照会（名前付き HttpClient "monitor"）は 5 秒で打ち切る（巡回を長く塞がない。IADR-0095 と同じ作法）。
    [Fact]
    public void 市場監視の照会は5秒で打ち切る()
    {
        using var factory = new Factory(
            new Dictionary<string, string?> { ["MarketMonitor:BaseUrl"] = "http://market-monitor-service:8080" },
            metrics: null,
            stubHandlers: false);
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("monitor").Timeout
            .Should().Be(TimeSpan.FromSeconds(5));
    }

    // T-10-1457: 日次上限（Collection:Source:Finnhub:DailyRequestLimit）が未設定なのは平常であり、警告しない（情報ログ）。
    [Fact]
    public void 日次上限が未設定でも警告しない()
    {
        var logs = new CapturingLoggerFactory();

        InformationSourceFactory.Create(
            new CollectionSourceOptions { Provider = "finnhub", Finnhub = new FinnhubOptions { ApiKey = "key", Symbols = ["AAPL"] } },
            new HttpClient(new CannedHandler("{}")),
            new FixedClock(),
            TimeProvider.System,
            logs);

        logs.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning && e.Message.Contains("日次", StringComparison.Ordinal));
        logs.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.Contains("ADR-0043", StringComparison.Ordinal));
    }

    private sealed class Factory(Dictionary<string, string?> settings, BusinessMetrics? metrics, bool stubHandlers = true)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = "amqp://localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                // External（run-once 駆動）にして in-process の巡回を走らせない（取得は試験から 1 回だけ呼ぶ）。
                ["Collection:PollIntervalSeconds"] = "3600",
                ["Collection:Trigger"] = "External",
            }));
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
            builder.ConfigureServices(services =>
            {
                services.DisableAllExternalWolverineTransports();
                if (metrics is not null)
                    services.AddSingleton(metrics);
                if (!stubHandlers)
                    return;
                services.AddHttpClient("monitor").ConfigurePrimaryHttpMessageHandler(() => new CannedHandler(
                    """[{"symbol":"AAPL","market":1},{"symbol":"7203","market":0},{"symbol":"MSFT","market":1}]"""));
                services.AddHttpClient("collection").ConfigurePrimaryHttpMessageHandler(() => new CannedHandler(
                    """{"c":150.25,"h":151.0,"l":149.0,"o":149.5,"pc":148.0,"t":1720000000}"""));
                services.AddSingleton<IServiceAccessTokenProvider>(new FakeTokenProvider());
            });
        }
    }

    private sealed class FakeTokenProvider : IServiceAccessTokenProvider
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("fake-service-token");
    }

    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 28, 14, 0, 0, TimeSpan.Zero);
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                    return [.. _entries];
            }
        }

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
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (entries)
                    entries.Add(new LogEntry(logLevel, formatter(state, exception)));
            }
        }
    }
}
