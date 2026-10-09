extern alias RiskManagementWorker;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetOpenPositions;
using MarketMonitorService.Common.Abstractions;
using MarketMonitorService.Features.MarketMonitor;
using MarketMonitorService.Hosted;
using MarketMonitorService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
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

// 🔴 T-10-2471（本番の組み立て）, FR-04, NFR-01, ADR-0043 決定 2 (b), #1281, IADR-0513［PR #1284 監査 🟡1］:
// **本番の Program.cs の組み立て**が、構成（MarketData:Provider・MarketData:Finnhub:RequestsPerMinute）から巡回の所要の
// Warning の余裕を組み、巡回（MonitorPollingService）へ渡すことを固定する。
//
// 余裕は省略可能な依存（未登録なら余裕 0）であるため、登録を消す・構成キーを取り違えると本番の余裕が黙って 0 に戻り、
// #1281（境界の構成で毎巡回 Warning）が再発する。単体テスト（CycleOverrunToleranceTests・MonitorPollingServiceTests）は
// 式と判定を固定するが、配線は組み立てでしか分からない。
// T-10-841 と同じく "risk" と "marketdata" の HttpClient の一次ハンドラだけを差し替え、常駐の巡回は外して同じ DI から
// 組み直した 1 回だけを回す（経過を測る時計だけは偽物を渡す）。
public class CycleOverrunToleranceCompositionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 15, 0, 0, TimeSpan.Zero);

    private const string CycleOverrunMessage = "市場監視の 1 巡回の所要";

    [Fact]
    public async Task T_10_2471_本番の組み立てはFinnhubの自制レートから余裕を組み巡回へ渡す()
    {
        await using var factory = new Factory();

        factory.Services.GetService<CycleOverrunTolerance>()
            .Should().Be(new CycleOverrunTolerance(TimeSpan.FromSeconds(5)), "Finnhub 12 回/分の 1 要求ぶんの送出間隔は 5 秒");

        // 余裕 5 秒: ちょうど 65 秒は出さず、66 秒は出す（余裕が渡っていなければ 65 秒でも出る）。
        (await RunCycleAsync(factory, TimeSpan.FromSeconds(65))).Should().NotContain(
            m => m.Contains(CycleOverrunMessage, StringComparison.Ordinal), "本番の組み立てでは余裕 5 秒が巡回へ渡る");
        (await RunCycleAsync(factory, TimeSpan.FromSeconds(66))).Should().ContainSingle(
            m => m.Contains(CycleOverrunMessage, StringComparison.Ordinal), "巡回間隔 ＋ 余裕を超えた巡回は警告する");
    }

    private static async Task<IReadOnlyList<string>> RunCycleAsync(Factory factory, TimeSpan quoteTakes)
    {
        var time = new SteppedTimeProvider();
        factory.Finnhub.OnRequest = () => time.Advance(quoteTakes);
        factory.PollingLog.Entries.Clear();

        // 常駐の巡回と同じ依存を本番の DI から解決して組む（経過を測る時計だけ偽物を渡す）。
        var polling = ActivatorUtilities.CreateInstance<MonitorPollingService>(factory.Services, (TimeProvider)time);
        await polling.RunOnceAsync(CancellationToken.None);

        return [.. factory.PollingLog.Warnings];
    }

    // リスク管理の GET /risk-controls/open-positions へ保有 1 件（AAPL）を返す。
    private sealed class RiskStub : HttpMessageHandler
    {
        private static readonly string Body = JsonSerializer.Serialize(
            new OpenPositionView[] { new("AAPL", Market.UnitedStates, TradeSide.Buy, 100, 350m, 300m) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri?.AbsolutePath == "/risk-controls/open-positions"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    // Finnhub /quote の一次ハンドラ。照会のたびに OnRequest（偽の時計を進める）を呼ぶ。
    private sealed class FinnhubStub : HttpMessageHandler
    {
        public Action OnRequest { get; set; } = () => { };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            OnRequest();
            var body = string.Create(CultureInfo.InvariantCulture, $$"""{"c":340,"h":0,"l":0,"o":0,"pc":0,"t":1790000000}""");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        public FinnhubStub Finnhub { get; } = new();

        public StopLossLivenessReporterTests.RecordingLogger<MonitorPollingService> PollingLog { get; } = new();

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
                ["MarketData:Finnhub:RequestsPerMinute"] = "12",
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
                services.AddHttpClient("risk").ConfigurePrimaryHttpMessageHandler(() => new RiskStub());
                services.AddHttpClient("marketdata").ConfigurePrimaryHttpMessageHandler(() => Finnhub);

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
                services.AddSingleton<ILogger<MonitorPollingService>>(PollingLog);
            });
        }
    }
}
