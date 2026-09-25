using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using Wolverine;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace TradeDecisionService.Tests;

// T-10-1057, NFR, FR-04, FR-10, MSP:ADR-0029, IADR-0284 決定 5（段 2）, IADR-0427 決定 5, #997 (#753):
// **本番の Program.cs の組み立て**で、`RiskManagement:Grpc` の有無がサイジング文脈・保有建玉の実装を切り替え、**既定は REST**
// であることを固定する。
//
// 🔴 **型を見るだけでなく、組み立てた実装で実際に呼ぶ。** 「配線を外しても全テストが緑」（#947 の形）を塞ぐため、
// 宣言ありの組み立てから解決したポートで偽の提供側（実 h2c）を呼び、**提供側が呼ばれた**ことまで見る。
// 構成は `UseSetting`（ホスト構成）で与える —— 登録時に読む値であり `ConfigureAppConfiguration` では届かない（段 1′ と同じ）。
public class RiskManagementGrpcWiringTests
{
    [Fact]
    public async Task T_10_1057_Grpc_を宣言すれば両ポートが_gRPC_実装になり実際に提供側を呼ぶ()
    {
        var behavior = new RiskReadStubBehavior
        {
            OpenPositions = (_, _) =>
            {
                var r = new Proto.GetOpenPositionsResponse();
                r.Positions.Add(new Proto.OpenPositionRow
                {
                    Symbol = "AAPL",
                    Market = Proto.Market.UnitedStates,
                    Side = Proto.TradeSide.Buy,
                    Quantity = 5,
                    EntryPrice = "200",
                    StopLossPrice = "190",
                });
                return Task.FromResult(r);
            },
        };
        await using var host = await RiskReadStubHost.StartAsync(behavior);
        using var factory = new Factory(grpc: host.Address, riskBaseUrl: "http://risk-rest-must-not-be-used");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var held = scope.ServiceProvider.GetRequiredService<IHeldPositionProvider>();
        var sizing = scope.ServiceProvider.GetRequiredService<ISizingContextProvider>();

        held.Should().BeOfType<GrpcHeldPositionProvider>("宣言があれば BaseUrl より gRPC を優先する");
        sizing.Should().BeOfType<GrpcSizingContextProvider>();
        held.IsEnabled.Should().BeTrue("gRPC も実結線（不明なら新規建てを見送る側）");

        (await held.GetSignedQuantityAsync("AAPL", Market.UnitedStates)).Should().Be(5);
        await sizing.GetContextAsync();
        behavior.Calls.Should().Be(2, "組み立てた実装が実際に偽の提供側を呼んだ");
    }

    // 🔴 監査の指摘（IADR-0427 決定 4 の固定）: 段 1 の全体前提条件（`Configuration:Grpc`）と段 2 のリスク管理
    // （`RiskManagement:Grpc`）を**別々の宛先**に宣言しても、それぞれの照会が**自分の宛先にだけ**届く。
    // 誰かが後で `GrpcChannel` を DI へ裸で登録すると、前提条件の照会（`GetRequiredService<GrpcChannel>()`）が後勝ちで
    // リスク管理の宛先へ飛び、UNIMPLEMENTED → 既定値（未解決）へ黙って倒れる。その形をここで赤にする。
    [Fact]
    public async Task T_10_1057_前提条件とリスク管理の_gRPC_は別々の宛先に並存し互いの照会が混ざらない()
    {
        await using var configurationHost = await GrpcStubHost.StartAsync(StubAssumptions.AlwaysOk());
        var riskBehavior = new RiskReadStubBehavior
        {
            OpenPositions = (_, _) =>
            {
                var r = new Proto.GetOpenPositionsResponse();
                r.Positions.Add(new Proto.OpenPositionRow
                {
                    Symbol = "AAPL",
                    Market = Proto.Market.UnitedStates,
                    Side = Proto.TradeSide.Buy,
                    Quantity = 4,
                    EntryPrice = "200",
                    StopLossPrice = "190",
                });
                return Task.FromResult(r);
            },
        };
        await using var riskHost = await RiskReadStubHost.StartAsync(riskBehavior);
        using var factory = new Factory(grpc: riskHost.Address, riskBaseUrl: null, configurationGrpc: configurationHost.Address);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var assumptions = await scope.ServiceProvider.GetRequiredService<IAssumptionsProvider>().GetCurrentAsync();
        var held = await scope.ServiceProvider.GetRequiredService<IHeldPositionProvider>()
            .GetSignedQuantityAsync("AAPL", Market.UnitedStates);
        await scope.ServiceProvider.GetRequiredService<ISizingContextProvider>().GetContextAsync();

        assumptions.IsResolved.Should().BeTrue("前提条件の照会は設定管理の宛先へ届いて解決する");
        assumptions.Version.Should().Be(3);
        held.Should().Be(4, "保有建玉の照会はリスク管理の宛先へ届く");
        configurationHost.Stub.Calls.Should().Be(1, "設定管理の宛先には前提条件の照会だけが届く");
        riskBehavior.Calls.Should().Be(2, "リスク管理の宛先にはリスク管理の読み取り（保有建玉・サイジング文脈）だけが届く");
    }

    // 陰性対照 1: 宣言が無ければ従来どおり REST（輸送そのものが登録されない＝バイト等価）。
    [Fact]
    public void T_10_1057_宣言が無ければ_REST_のまま()
    {
        using var factory = new Factory(grpc: null, riskBaseUrl: "http://risk");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetService<RiskManagementGrpcTransport>().Should().BeNull();
        scope.ServiceProvider.GetRequiredService<IHeldPositionProvider>().Should().BeOfType<HttpHeldPositionProvider>();
        scope.ServiceProvider.GetRequiredService<ISizingContextProvider>().Should().BeOfType<HttpSizingContextProvider>();
    }

    // 陰性対照 2: どちらも無ければ安全既定（未結線）のまま。
    [Fact]
    public void T_10_1057_どちらも無ければ安全既定のまま()
    {
        using var factory = new Factory(grpc: null, riskBaseUrl: null);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHeldPositionProvider>().Should().BeOfType<NoOpHeldPositionProvider>();
        scope.ServiceProvider.GetRequiredService<ISizingContextProvider>().Should().BeOfType<PlaceholderSizingContextProvider>();
    }

    // 陰性対照 3: 宣言してあるのに使えない宛先は**起動時に落とす**（黙って REST へ戻さない。段 1 と同じ）。
    [Theory]
    [InlineData("https://risk-management-service:8081")]
    [InlineData("risk-management-service:8081")]
    [InlineData("/relative")]
    public void T_10_1057_使えない宛先は起動時に落とす(string address)
    {
        using var factory = new Factory(grpc: address, riskBaseUrl: "http://risk");

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*RiskManagement:Grpc*");
    }

    private sealed class Factory(string? grpc, string? riskBaseUrl, string? configurationGrpc = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            if (grpc is not null)
                builder.UseSetting("RiskManagement:Grpc", grpc);
            if (configurationGrpc is not null)
                builder.UseSetting("Configuration:Grpc", configurationGrpc);
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["RabbitMq:ConnectionString"] = "amqp://localhost",
                    ["Otlp:Endpoint"] = "http://localhost:4317",
                };
                if (riskBaseUrl is not null)
                    settings["RiskManagement:BaseUrl"] = riskBaseUrl;
                cfg.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
