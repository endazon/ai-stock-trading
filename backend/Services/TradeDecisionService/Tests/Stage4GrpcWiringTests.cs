using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using TradeDecisionService.Infrastructure.ExternalServices;
using Wolverine;
using Xunit;
using MonitorProto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;
using ReportProto = AiStockTrading.Shared.Grpc.Report.V1;

namespace TradeDecisionService.Tests;

// T-10-1697, NFR, FR-02, FR-04, FR-07, FR-15, MSP:ADR-0029, IADR-0284 決定 5（段 4）, IADR-0446 決定 5, #1061 (#753):
// **本番の Program.cs の組み立て**で、`Reports:Grpc` / `MarketMonitor:Grpc` の有無が日報の方針・監視銘柄・当時の監視銘柄の実装を
// 切り替え、**既定は REST** であることを固定する。🔴 型を見るだけでなく、組み立てた実装で**実際に呼び**、偽の提供側の rpc ごとの
// 呼ばれた回数が**呼んだ分だけ増える**ことまで見る（常駐の巡回が起動直後に呼び得るため、絶対値ではなく差分で数える）。
public class Stage4GrpcWiringTests
{
    [Fact]
    public async Task T_10_1697_Grpc_を宣言すれば_3_つのポートが_gRPC_実装になり実際に提供側を呼ぶ()
    {
        var behavior = new Stage4ReadStubBehavior
        {
            Policy = Stage4ReadStubBehavior.Returns(new ReportProto.GetConfirmedDailyPolicyResponse
            {
                Policy = new ReportProto.DailyPolicyRecord { Date = "2026-09-10", Summary = "押し目買い" },
            }),
            Watchlist = Stage4ReadStubBehavior.Returns(new MonitorProto.GetWatchlistResponse
            {
                Items = { new MonitorProto.WatchlistItem { Symbol = "AAPL", Market = MonitorProto.Market.UnitedStates } },
            }),
            AsOf = Stage4ReadStubBehavior.Returns(new MonitorProto.GetWatchlistAsOfResponse { Reconstructed = false, Reason = "試験" }),
        };
        await using var host = await Stage4ReadStubHost.StartAsync(behavior);
        using var factory = new Factory(new()
        {
            ["Reports:Grpc"] = host.Address,
            ["MarketMonitor:Grpc"] = host.Address,
        }, restBaseUrls: true);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var policy = scope.ServiceProvider.GetRequiredService<IDailyPolicyProvider>();
        var watchlist = scope.ServiceProvider.GetRequiredService<IWatchlistProvider>();
        var asOf = scope.ServiceProvider.GetRequiredService<IAsOfWatchlistSource>();

        policy.Should().BeOfType<GrpcDailyPolicyProvider>("宣言があれば BaseUrl より gRPC を優先する");
        watchlist.Should().BeOfType<GrpcWatchlistProvider>();
        asOf.Should().BeOfType<GrpcAsOfWatchlistSource>();

        var (p0, w0, a0) = (behavior.PolicyCalls, behavior.WatchlistCalls, behavior.AsOfCalls);
        (await policy.GetCurrentAsync())!.Summary.Should().Be("押し目買い");
        (await watchlist.GetAuthoritativeWatchlistAsync()).Should().Equal(new WatchedSymbol("AAPL", Market.UnitedStates));
        (await asOf.GetWatchlistAtAsync(DateTimeOffset.UtcNow)).Reason.Should().Be("試験");

        (behavior.PolicyCalls - p0, behavior.WatchlistCalls - w0, behavior.AsOfCalls - a0)
            .Should().Be((1, 1, 1), "組み立てた実装が実際に偽の提供側を呼んだ");
    }

    // 陰性対照 1: 宣言が無ければ従来どおり REST（輸送そのものが登録されない）。
    [Fact]
    public void T_10_1697_宣言が無ければ_REST_のまま()
    {
        using var factory = new Factory([], restBaseUrls: true);
        _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();

        factory.Services.GetService<ReportsGrpcTransport>().Should().BeNull();
        factory.Services.GetService<MarketMonitorGrpcTransport>().Should().BeNull();
        scope.ServiceProvider.GetRequiredService<IDailyPolicyProvider>().Should().BeOfType<HttpDailyPolicyProvider>();
        scope.ServiceProvider.GetRequiredService<IWatchlistProvider>().Should().BeOfType<HttpWatchlistProvider>();
        scope.ServiceProvider.GetRequiredService<IAsOfWatchlistSource>().Should().BeOfType<HttpAsOfWatchlistSource>();
    }

    // 陰性対照 2: 片方だけ宣言すれば、その提供側のポートだけが gRPC になる（輸送が混ざらない）。
    [Fact]
    public void T_10_1697_報告書だけを宣言すれば監視銘柄は_REST_のまま()
    {
        using var factory = new Factory(new() { ["Reports:Grpc"] = "http://report-service:8081" }, restBaseUrls: true);
        _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IDailyPolicyProvider>().Should().BeOfType<GrpcDailyPolicyProvider>();
        scope.ServiceProvider.GetRequiredService<IWatchlistProvider>().Should().BeOfType<HttpWatchlistProvider>();
        scope.ServiceProvider.GetRequiredService<IAsOfWatchlistSource>().Should().BeOfType<HttpAsOfWatchlistSource>();
    }

    // 陰性対照 3: 宣言してあるのに使えない宛先は起動時に落とす（黙って REST へ戻さない）。
    [Theory]
    [InlineData("Reports:Grpc", "https://report-service:8081")]
    [InlineData("MarketMonitor:Grpc", "market-monitor-service:8081")]
    public void T_10_1697_使えない宛先は起動時に落とす(string key, string address)
    {
        using var factory = new Factory(new() { [key] = address }, restBaseUrls: false);

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{key}*");
    }

    private sealed class Factory(Dictionary<string, string> grpc, bool restBaseUrls) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            foreach (var (k, v) in grpc)
                builder.UseSetting(k, v);
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["RabbitMq:ConnectionString"] = "amqp://localhost",
                    ["Otlp:Endpoint"] = "http://localhost:4317",
                };
                if (restBaseUrls)
                {
                    settings["Reports:BaseUrl"] = "http://report-rest-must-not-be-used";
                    settings["MarketMonitor:BaseUrl"] = "http://monitor-rest-must-not-be-used";
                }

                cfg.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
