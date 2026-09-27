using AwesomeAssertions;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using CostProto = AiStockTrading.Shared.Grpc.CostControl.V1;
using MonitorProto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;

namespace InformationCollectionService.Tests;

// T-10-1697, NFR, FR-01, FR-13, MSP:ADR-0029, IADR-0284 決定 5（段 4）, IADR-0446 決定 5, #1061 (#753):
// **本番の Program.cs の組み立て**で、`MarketMonitor:Grpc` / `CostControl:Grpc` の有無が監視銘柄の読み手と統制ゲートの実装を切り替え、
// **既定は REST** であることを固定する。🔴 型を見るだけでなく、組み立てた実装で**実際に呼び**、偽の提供側の rpc ごとの呼ばれた回数が
// **呼んだ分だけ増える**ことまで見る。常駐の巡回（in-process のポーリング）は `Collection:Trigger=External` で止める —— 差分で数えても、
// 巡回が試験の呼び出しと同時に走ると増分が 2 になる（段 2 の #1010 と同じ形。全件の実行で実測）。
public class Stage4GrpcWiringTests
{
    [Fact]
    public async Task T_10_1697_Grpc_を宣言すれば監視銘柄の読み手と統制ゲートが_gRPC_実装になり実際に提供側を呼ぶ()
    {
        var behavior = new Stage4ReadStubBehavior
        {
            Watchlist = Stage4ReadStubBehavior.Returns(new MonitorProto.GetWatchlistResponse
            {
                Items =
                {
                    new MonitorProto.WatchlistItem { Symbol = "AAPL", Market = MonitorProto.Market.UnitedStates },
                    new MonitorProto.WatchlistItem { Symbol = "7203", Market = MonitorProto.Market.Japan },
                },
            }),
            Cost = Stage4ReadStubBehavior.Returns(new CostProto.GetCostStateResponse { IsHalted = false, IntervalMultiplier = "2" }),
        };
        await using var host = await Stage4ReadStubHost.StartAsync(behavior);
        using var factory = new Factory(new()
        {
            ["MarketMonitor:Grpc"] = host.Address,
            ["CostControl:Grpc"] = host.Address,
        }, restBaseUrls: true);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<ICostControlGate>();
        var selector = factory.Services.GetRequiredService<FinnhubSymbolSelector>();

        gate.Should().BeOfType<GrpcCostControlGate>("宣言があれば BaseUrl より gRPC を優先する");
        selector.FollowsWatchlist.Should().BeTrue();

        var (w0, c0) = (behavior.WatchlistCalls, behavior.CostCalls);
        (await gate.GetAsync()).Should().Be(new CostControlGate(false, 2m));
        await selector.RefreshAsync();
        selector.Current.Should().Contain("AAPL").And.NotContain("7203", "Finnhub の対象は米国の銘柄だけ");

        (behavior.WatchlistCalls - w0, behavior.CostCalls - c0).Should().Be((1, 1), "組み立てた実装が実際に偽の提供側を呼んだ");
    }

    // 陰性対照 1: 宣言が無ければ従来どおり REST（輸送そのものが登録されない）。
    [Fact]
    public void T_10_1697_宣言が無ければ_REST_のまま()
    {
        using var factory = new Factory([], restBaseUrls: true);
        _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();

        factory.Services.GetService<MarketMonitorGrpcTransport>().Should().BeNull();
        factory.Services.GetService<CostControlGrpcTransport>().Should().BeNull();
        scope.ServiceProvider.GetRequiredService<ICostControlGate>().Should().BeOfType<HttpCostControlGate>();
    }

    // 陰性対照 2: 市場監視だけを宣言すれば費用統制は REST のまま（輸送が混ざらない）。
    [Fact]
    public void T_10_1697_市場監視だけを宣言すれば費用統制は_REST_のまま()
    {
        using var factory = new Factory(new() { ["MarketMonitor:Grpc"] = "http://market-monitor-service:8081" }, restBaseUrls: true);
        _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();

        factory.Services.GetService<MarketMonitorGrpcTransport>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ICostControlGate>().Should().BeOfType<HttpCostControlGate>();
    }

    // 陰性対照 3: 宣言してあるのに使えない宛先は起動時に落とす（黙って REST へ戻さない）。
    [Theory]
    [InlineData("MarketMonitor:Grpc", "https://market-monitor-service:8081")]
    [InlineData("CostControl:Grpc", "cost-control-service:8081")]
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
                    ["Collection:PollIntervalSeconds"] = "3600",
                    // 常駐の巡回を止める（起動直後の巡回が統制ゲート・監視銘柄を照会し、試験の呼び出しと競合する。実測で費用統制が 2 回）。
                    ["Collection:Trigger"] = "External",
                };
                if (restBaseUrls)
                {
                    settings["MarketMonitor:BaseUrl"] = "http://monitor-rest-must-not-be-used";
                    settings["CostControl:BaseUrl"] = "http://cost-rest-must-not-be-used";
                }

                cfg.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
