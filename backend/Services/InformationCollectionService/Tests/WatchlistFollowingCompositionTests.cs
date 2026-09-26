using System.Net;
using System.Text;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace InformationCollectionService.Tests;

// FR-01, FR-02, FR-13, #1015, IADR-0435: 本番の組み立て（Program.cs）で、Finnhub の対象銘柄が市場監視の監視銘柄に追随すること
// （T-10-1471）と、照会が trading-service のトークン付きで監視銘柄の照会口へ行くこと（T-10-1472）。
// 外部へは出ない —— 市場監視・Finnhub の名前付き HttpClient の一次ハンドラを差し替え、トークンの取得も差し替える。
public class WatchlistFollowingCompositionTests
{
    private const string MonitorBody = """[{"symbol":"AAPL","market":1},{"symbol":"7203","market":0},{"symbol":"MSFT","market":1}]""";

    // T-10-1471 / T-10-1472: 結線時は装飾が挟まり、固定リストが空でも Finnhub が有効で、1 巡回の取得で監視銘柄の米国の銘柄を問い合わせる。
    [Fact]
    public async Task 結線時は監視銘柄の米国の銘柄をトークン付きの照会で決めて収集する()
    {
        var monitor = new RecordingHandler(_ => MonitorBody);
        var finnhub = new RecordingHandler(_ => """{"c":150.25,"h":151.0,"l":149.0,"o":149.5,"pc":148.0,"t":1720000000}""");
        using var factory = new Factory(
            new Dictionary<string, string?>
            {
                ["MarketMonitor:BaseUrl"] = "http://market-monitor-service:8080",
                ["Collection:Source:Provider"] = "finnhub",
                ["Collection:Source:Finnhub:ApiKey"] = "key",
                ["ServiceAuth:ClientId"] = "ai-stock-trading-service",
                ["ServiceAuth:ClientSecret"] = "not-a-secret",
                ["ServiceAuth:TokenEndpoint"] = "http://keycloak.invalid/token",
            },
            monitor,
            finnhub);
        _ = factory.CreateClient();

        var fetcher = factory.Services.GetRequiredService<ISourceFetcher>()
            .Should().BeOfType<WatchlistFollowingSourceFetcher>().Which;
        fetcher.Inner.Should().BeOfType<SourceFetchRunner>().Which.SourceNames.Should().Equal("finnhub");

        var result = await fetcher.FetchAllAsync();

        result.Items.Select(i => i.Symbol).Should().Equal("AAPL", "MSFT");
        finnhub.Paths.Should().OnlyContain(p => p.EndsWith("/quote", StringComparison.Ordinal));
        finnhub.Queries.Should().Equal("?symbol=AAPL", "?symbol=MSFT");
        monitor.Paths.Should().Equal("/monitor/watchlist");
        monitor.Authorizations.Should().Equal("Bearer fake-service-token");
    }

    // T-10-1471（対）: 未結線（既定）は従来どおり —— 装飾を挟まず、固定リストが空なら Finnhub を有効化しない。
    [Fact]
    public void 未結線で固定リストが空ならFinnhubは有効化されず装飾も挟まない()
    {
        using var factory = new Factory(
            new Dictionary<string, string?>
            {
                ["Collection:Source:Provider"] = "finnhub,fred",
                ["Collection:Source:Finnhub:ApiKey"] = "key",
                ["Collection:Source:Fred:ApiKey"] = "key",
                ["Collection:Source:Fred:SeriesIds:0"] = "DGS10",
            },
            new RecordingHandler(_ => "[]"),
            new RecordingHandler(_ => "{}"));
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<ISourceFetcher>()
            .Should().BeOfType<SourceFetchRunner>().Which.SourceNames.Should().Equal("fred");
        factory.Services.GetRequiredService<FinnhubSymbolSelector>().FollowsWatchlist.Should().BeFalse();
    }

    // T-10-1471（対）: 結線しても Finnhub 系が無ければ照会しない（装飾を挟まない）。
    [Fact]
    public void 結線してもFinnhub系が無ければ装飾を挟まない()
    {
        using var factory = new Factory(
            new Dictionary<string, string?>
            {
                ["MarketMonitor:BaseUrl"] = "http://market-monitor-service:8080",
                ["Collection:Source:Provider"] = "fred",
                ["Collection:Source:Fred:ApiKey"] = "key",
                ["Collection:Source:Fred:SeriesIds:0"] = "DGS10",
            },
            new RecordingHandler(_ => "[]"),
            new RecordingHandler(_ => "{}"));
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<ISourceFetcher>().Should().BeOfType<SourceFetchRunner>();
    }

    private sealed class Factory(
        Dictionary<string, string?> settings, HttpMessageHandler monitor, HttpMessageHandler finnhub)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RabbitMq:ConnectionString"] = "amqp://localhost",
                    ["Otlp:Endpoint"] = "http://localhost:4317",
                    // in-process の巡回は起動直後に 1 回走るため、External（run-once 駆動）にして巡回させない
                    // （取得は試験から 1 回だけ呼ぶ）。
                    ["Collection:PollIntervalSeconds"] = "3600",
                    ["Collection:Trigger"] = "External",
                });
            });
            // 登録時に構成を読む配線（サービストークンの付与）があるため、Program.cs の本文より前に見える UseSetting で与える。
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
            builder.ConfigureServices(services =>
            {
                services.DisableAllExternalWolverineTransports();
                services.AddHttpClient("monitor").ConfigurePrimaryHttpMessageHandler(() => monitor);
                services.AddHttpClient("collection").ConfigurePrimaryHttpMessageHandler(() => finnhub);
                services.AddSingleton<IServiceAccessTokenProvider>(new FakeTokenProvider());
            });
        }
    }

    private sealed class FakeTokenProvider : IServiceAccessTokenProvider
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("fake-service-token");
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly List<string> _paths = [];
        private readonly List<string> _queries = [];
        private readonly List<string> _authorizations = [];

        public IReadOnlyList<string> Paths { get { lock (_gate) return [.. _paths]; } }

        public IReadOnlyList<string> Queries { get { lock (_gate) return [.. _queries]; } }

        public IReadOnlyList<string> Authorizations { get { lock (_gate) return [.. _authorizations]; } }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _paths.Add(request.RequestUri!.AbsolutePath);
                _queries.Add(request.RequestUri!.Query);
                _authorizations.Add(request.Headers.Authorization?.ToString() ?? "");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json"),
            });
        }
    }
}
