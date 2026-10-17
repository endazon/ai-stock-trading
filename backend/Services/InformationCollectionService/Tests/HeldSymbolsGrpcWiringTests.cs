using AwesomeAssertions;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using MonitorProto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;
using RiskProto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace InformationCollectionService.Tests;

// T-10-2764, NFR, FR-01, FR-02, MSP:ADR-0029, IADR-0547 決定 1, IADR-0557 決定 2, #1388 (#753):
// **本番の Program.cs の組み立て**で、`RiskManagement:Grpc` の有無が収集の対象へ足す保有銘柄の読み手を切り替え、**既定は REST** であることを固定する。
// 🔴 型ではなく振る舞いで見る（読み手は選択器の private な引数）: 組み立てた選択器を実際に回し、偽の提供側の `GetOpenPositions` が
// **呼んだ分だけ増え**、保有だけの米国銘柄が対象に入ることを見る。常駐の巡回は `Collection:Trigger=External` で止める（Stage4GrpcWiringTests と同じ）。
public class HeldSymbolsGrpcWiringTests
{
    private static readonly MonitorProto.GetWatchlistResponse Watchlist = new()
    {
        Items = { new MonitorProto.WatchlistItem { Symbol = "AAPL", Market = MonitorProto.Market.UnitedStates } },
    };

    private static RiskProto.GetOpenPositionsResponse HeldMsft() => new()
    {
        Positions =
        {
            new RiskProto.OpenPositionRow
            {
                Symbol = "MSFT", Market = RiskProto.Market.UnitedStates, Side = RiskProto.TradeSide.Buy, Quantity = 3,
            },
        },
    };

    [Fact]
    public async Task T_10_2764_宣言すれば保有銘柄を_gRPC_で読み_REST_の宛先より優先する()
    {
        var behavior = new Stage4ReadStubBehavior
        {
            Watchlist = Stage4ReadStubBehavior.Returns(Watchlist),
            OpenPositions = Stage4ReadStubBehavior.Returns(HeldMsft()),
        };
        await using var host = await Stage4ReadStubHost.StartAsync(behavior);
        using var factory = new Factory(new()
        {
            ["MarketMonitor:Grpc"] = host.Address,
            ["RiskManagement:Grpc"] = host.Address,
        });
        _ = factory.CreateClient();

        factory.Services.GetService<RiskManagementGrpcTransport>().Should().NotBeNull();
        var selector = factory.Services.GetRequiredService<FinnhubSymbolSelector>();
        var before = behavior.OpenPositionsCalls;
        await selector.RefreshAsync();

        selector.Current.Should().Contain("MSFT", "保有だけの米国銘柄も収集の対象に入る（gRPC で読めた）").And.Contain("AAPL");
        (behavior.OpenPositionsCalls - before).Should().Be(1, "組み立てた読み手が実際に偽の提供側を呼んだ");
    }

    // 陰性対照: 宣言が無ければ輸送そのものが登録されない（REST のまま）。gRPC の提供側は呼ばれない。
    [Fact]
    public async Task T_10_2764_宣言が無ければ_REST_のまま()
    {
        var behavior = new Stage4ReadStubBehavior
        {
            Watchlist = Stage4ReadStubBehavior.Returns(Watchlist),
            OpenPositions = Stage4ReadStubBehavior.Returns(HeldMsft()),
        };
        await using var host = await Stage4ReadStubHost.StartAsync(behavior);
        using var factory = new Factory(new() { ["MarketMonitor:Grpc"] = host.Address });
        _ = factory.CreateClient();

        factory.Services.GetService<RiskManagementGrpcTransport>().Should().BeNull();
        await factory.Services.GetRequiredService<FinnhubSymbolSelector>().RefreshAsync();
        behavior.OpenPositionsCalls.Should().Be(0);
    }

    // 陰性対照: 宣言してあるのに使えない宛先は起動時に落とす（黙って REST へ戻さない）。
    [Theory]
    [InlineData("https://risk-management-service:8081")]
    [InlineData("risk-management-service:8081")]
    public void T_10_2764_使えない宛先は起動時に落とす(string address)
    {
        using var factory = new Factory(new() { ["RiskManagement:Grpc"] = address });

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*RiskManagement:Grpc*");
    }

    private sealed class Factory(Dictionary<string, string> grpc) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            foreach (var (k, v) in grpc)
                builder.UseSetting(k, v);
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = "amqp://localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Collection:PollIntervalSeconds"] = "3600",
                ["Collection:Trigger"] = "External",
                // 🔴 REST の宛先を置いても gRPC が優先されること（宣言があるとき）。宣言が無いときは REST の読み手が選ばれ、
                // この到達できない宛先へ照会して「不明」に倒れる（gRPC の提供側は呼ばれない）。
                ["RiskManagement:BaseUrl"] = "http://risk-rest-must-not-be-used",
            }));
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
