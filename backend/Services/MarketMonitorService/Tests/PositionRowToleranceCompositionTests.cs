extern alias RiskManagementWorker;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetOpenPositions;
using MarketMonitorService.Common.Abstractions;
using MarketMonitorService.Features.MarketMonitor;
using MarketMonitorService.Hosted;
using MarketMonitorService.Infrastructure.ExternalServices;
using MarketMonitorService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Metrics;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Xunit;

namespace MarketMonitorService.Tests;

// 🔴 T-10-840 / T-10-841, FR-03, FR-10, #957, IADR-0399: **本番の Program.cs の組み立て**で、保有照会の 1 行の不正が
// 巡回全体（全建玉の損切り検知・生存要約）を止めないことを確かめる。
//
// 単体テスト（HttpPositionStoreTests・MarketMonitorServiceTests・FinnhubMarketDataSourceTests）は層ごとの守りを固定するが、
// 「本番の配線が実際にその実装を組み、計器を渡し、実運用の市況源（Finnhub）とつながっている」ことは組み立てでしか分からない。
// ここでは RiskManagement:BaseUrl と MarketData:Provider=finnhub を与えて Program.cs をそのまま起こし、"risk" と "marketdata" の
// HttpClient の**一次ハンドラだけ**を差し替える（アダプタの選択・計器・委譲ハンドラの連鎖は本番のまま）。時刻と市場の開閉だけは
// 決定的にするため注入し、常駐の巡回（MonitorPollingService）は外して同じ DI から組み直した 1 回だけを回す。
//
// 応答は**送り手の本物の型（OpenPositionView）を web 既定で直列化**し、1 行から銘柄を、1 行から損切りラインを消す
// （送り手の改名を片方だけ先に配備した窓の再現。PR #959 監査の実測: 是正前は Finnhub の ArgumentNullException で巡回全体が止まった）。
public class PositionRowToleranceCompositionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 15, 0, 0, TimeSpan.Zero);

    private static string SenderBodyWithBrokenRows()
    {
        IReadOnlyList<OpenPositionView> views =
        [
            new("AAPL", Market.UnitedStates, TradeSide.Buy, 100, 350m, 338.51m),   // 健全・現在値 330 で到達
            new("MSFT", Market.UnitedStates, TradeSide.Buy, 5, 2_000m, 1_900m),    // 銘柄を消す（識別できない）
            new("TSLA", Market.UnitedStates, TradeSide.Sell, 5, 240m, 252m),       // 損切りラインを消す（近似 247.20）
        ];
        var array = JsonSerializer.SerializeToNode(views, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsArray();
        array[1]!.AsObject().Remove("symbol");
        array[2]!.AsObject().Remove("stopLossPrice");
        return array.ToJsonString();
    }

    [Fact]
    public async Task T_10_840_本番の組み立てで識別できない行と損切りラインの無い行があっても健全な行の到達が出て近似の行も評価される()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        var finnhub = new FinnhubStub(new Dictionary<string, decimal> { ["AAPL"] = 330m, ["TSLA"] = 239m });
        await using var factory = new Factory(new RiskStub(SenderBodyWithBrokenRows()), finnhub);

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IPositionStore>().Should().BeOfType<HttpPositionStore>(
            "本番の配線（RiskManagement:BaseUrl あり）が選ぶ実装を試す");
        var monitor = scope.ServiceProvider.GetRequiredService<MarketMonitorAppService>();

        var result = await monitor.EvaluateRoundAsync();

        result.StopLosses.Should().ContainSingle().Which.Symbol.Should().Be("AAPL", "健全な行の到達は 1 行の不正に巻き込まれない");
        var tsla = result.StopLossEvaluations.Single(e => e.Symbol == "TSLA");
        tsla.StopLossApproximated.Should().BeTrue();
        tsla.StopLossPrice.Should().Be(247.20m, "ライン 0 ではなく送り手と同じ近似（240 × 1.03）で評価する");
        tsla.Price.Should().Be(239m);
        result.StopLossEvaluations.Should().HaveCount(2, "識別できない行は評価に渡さない");
        finnhub.RequestedSymbols.Should().BeEquivalentTo(["AAPL", "TSLA"], "銘柄の無い照会を市況源へ出さない");
        capture.TagValuesOf(BusinessMetricNames.MarketMonitorPositionRowsDegraded, BusinessMetricNames.TagReason)
            .Should().Contain("identity-missing").And.Contain("stop-line-approximated");
        factory.StoreLog.Entries.Should().ContainSingle(e => e.Level == LogLevel.Critical);
    }

    [Fact]
    public async Task T_10_841_本番の組み立てで1行の不正があっても巡回は例外なく終わり健全な行の生存要約が出る()
    {
        var finnhub = new FinnhubStub(new Dictionary<string, decimal> { ["AAPL"] = 340m, ["TSLA"] = 239m });
        await using var factory = new Factory(new RiskStub(SenderBodyWithBrokenRows()), finnhub);
        _ = factory.Services; // ホスト起動

        // 常駐の巡回と同じ依存（スコープ・市場の開閉・時計・構成・生存要約）を本番の DI から解決して組む。
        var polling = ActivatorUtilities.CreateInstance<MonitorPollingService>(factory.Services);

        var act = () => polling.RunOnceAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("1 行の不正で巡回全体を落とさない（是正前は Finnhub の ArgumentNullException で落ちた）");
        factory.StoreLog.Entries.Should().ContainSingle(e => e.Level == LogLevel.Critical);
        var summary = factory.LivenessLog.Informations
            .Should().ContainSingle(m => m.Contains("損切り評価は稼働中", StringComparison.Ordinal)).Which;
        summary.Should().Contain("保有 2 件").And.Contain("AAPL/UnitedStates").And.Contain("TSLA/UnitedStates")
            .And.Contain("ライン=247.20（近似");
    }

    // リスク管理の GET /risk-controls/open-positions だけに 200 と本文を返す。
    private sealed class RiskStub(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri?.AbsolutePath == "/risk-controls/open-positions"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    // Finnhub /quote の一次ハンドラ。照会された銘柄を記録し、既知の銘柄だけ現在値を返す（他は Finnhub と同じ 200＋全項目 0）。
    private sealed class FinnhubStub(IReadOnlyDictionary<string, decimal> prices) : HttpMessageHandler
    {
        private readonly List<string> _requested = [];

        public IReadOnlyList<string> RequestedSymbols
        {
            get
            {
                lock (_requested)
                    return [.. _requested];
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query ?? string.Empty;
            var symbol = Uri.UnescapeDataString(query.StartsWith("?symbol=", StringComparison.Ordinal) ? query["?symbol=".Length..] : query);
            lock (_requested)
                _requested.Add(symbol);
            var price = prices.TryGetValue(symbol, out var p) ? p : 0m;
            var body = string.Create(CultureInfo.InvariantCulture, $$"""{"c":{{price}},"h":0,"l":0,"o":0,"pc":0,"t":1790000000}""");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler riskHandler, HttpMessageHandler finnhubHandler) : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        public StopLossLivenessReporterTests.RecordingLogger<HttpPositionStore> StoreLog { get; } = new();

        public StopLossLivenessReporterTests.RecordingLogger<StopLossLivenessReporter> LivenessLog { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = "amqp://localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test",
                ["RiskManagement:BaseUrl"] = "http://risk",
                ["MarketData:Provider"] = "finnhub",
                ["MarketData:Finnhub:ApiKey"] = "test-key",
                ["MarketData:Finnhub:BaseUrl"] = "http://finnhub",
                ["MarketData:Finnhub:RequestsPerMinute"] = "600",
            }));

            builder.ConfigureServices(services =>
            {
                var toRemove = services
                    .Where(d => d.ServiceType == typeof(DbContextOptions<MarketMonitorDbContext>)
                             || (d.ServiceType.IsGenericType
                                 && d.ServiceType.GetGenericTypeDefinition().FullName?
                                     .Contains("IDbContextOptionsConfiguration") == true
                                 && d.ServiceType.GenericTypeArguments.Length == 1
                                 && d.ServiceType.GenericTypeArguments[0] == typeof(MarketMonitorDbContext)))
                    .ToList();
                foreach (var d in toRemove) services.Remove(d);
                services.AddDbContext<MarketMonitorDbContext>(opt => opt.UseInMemoryDatabase(_dbName));

                // ADR-0013, IADR-0129, #354: 実 RabbitMQ へ接続しない。
                services.DisableAllExternalWolverineTransports();

                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            });

            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient("risk").ConfigurePrimaryHttpMessageHandler(() => riskHandler);
                services.AddHttpClient("marketdata").ConfigurePrimaryHttpMessageHandler(() => finnhubHandler);

                // 決定的にするのは時刻と市場の開閉だけ（どちらも注入点）。
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new FakeClock(Now));
                services.RemoveAll<IMarketSchedule>();
                services.AddSingleton<IMarketSchedule>(new FakeSchedule(open: true));

                // 常駐の巡回はテストの外で勝手に回さない（同じ DI から組み直して 1 回だけ回す）。
                var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType == typeof(MonitorPollingService)).ToList();
                foreach (var d in hosted) services.Remove(d);

                // ログの観測点（閉じた型の登録は ILogger<> の既定より優先される）。
                services.AddSingleton<ILogger<HttpPositionStore>>(StoreLog);
                services.AddSingleton<ILogger<StopLossLivenessReporter>>(LivenessLog);
            });
        }
    }
}
