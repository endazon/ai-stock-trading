using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using Wolverine;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.OrderExecution.V1;

namespace TradeDecisionService.Tests;

// T-10-2909, NFR, FR-04, FR-10, FR-15, MSP:ADR-0029, IADR-0559 決定 3, #1391 (#753):
// **本番の Program.cs の組み立て**で、`OrderExecution:Grpc` の有無が日足の口（出来高・ATR(14)・Stage 0 の当時の価格）と処理中の決済の照会口を
// 切り替え、**既定は REST** であることを固定する。🔴 型だけでなく、組み立てた実装で実際に呼び、偽の提供側の rpc ごとの呼ばれた回数が
// 呼んだ分だけ増えることまで見る（常駐の巡回が起動直後に呼び得るため、差分で数える）。
public class OrderExecutionGrpcWiringTests
{
    [Fact]
    public async Task T_10_2909_宣言すれば日足と処理中の決済を_gRPC_で読み_REST_の宛先より優先する()
    {
        var behavior = new OrderExecutionReadStubBehavior
        {
            InFlightCloses = OrderExecutionReadStubBehavior.Returns(new Proto.GetInFlightClosesResponse
            {
                Symbol = "AAPL",
                Market = Proto.Market.UnitedStates,
                CloseSide = Proto.TradeSide.Sell,
                Status = Proto.InFlightClosesState.Covered,
                InFlightQuantity = 10,
                OrderIds = { "o-1" },
            }),
        };
        await using var host = await OrderExecutionReadStubHost.StartAsync(behavior);
        using var factory = new Factory(new()
        {
            ["OrderExecution:Grpc"] = host.Address,
            ["DecisionVolume:Enabled"] = "true",
            // 🔴 REST の宛先を置いても gRPC が優先されること。
            ["OrderExecution:BaseUrl"] = "http://order-execution-rest-must-not-be-used",
        });
        _ = factory.CreateClient();

        factory.Services.GetService<OrderExecutionGrpcTransport>().Should().NotBeNull();
        using var scope = factory.Services.CreateScope();
        var inFlight = scope.ServiceProvider.GetRequiredService<IInFlightCloseProvider>();
        inFlight.Should().BeOfType<GrpcInFlightCloseProvider>();
        var dailyBars = scope.ServiceProvider.GetRequiredService<IDailyBarsProvider>();
        dailyBars.Should().BeOfType<CachedDailyBarsProvider>();

        var (c0, d0) = (behavior.InFlightClosesCalls, behavior.DailyBarsCalls);
        (await inFlight.GetAsync("AAPL", Market.UnitedStates, TradeSide.Sell, TestContext.Current.CancellationToken))!
            .Covered.Should().BeTrue("組み立てた読み手が偽の提供側の答えを読んだ");
        // 日足は AsOf の照会（キャッシュを通らない）で 1 回だけ撃つ。偽の提供側は空の応答（状態の未指定）なので取得できない＝null。
        (await dailyBars.GetConfirmedBarsAsOfAsync("AAPL", Market.UnitedStates, new DateOnly(2026, 9, 29), TestContext.Current.CancellationToken))
            .Should().BeNull();

        (behavior.InFlightClosesCalls - c0, behavior.DailyBarsCalls - d0)
            .Should().Be((1, 1), "組み立てた実装が実際に偽の提供側を呼んだ（日足の共有口も gRPC）");
    }

    // 陰性対照: 宣言が無ければ輸送そのものが登録されず、従来どおり REST（BaseUrl があるとき）。
    [Fact]
    public void T_10_2909_宣言が無ければ_REST_のまま()
    {
        using var factory = new Factory(new()
        {
            ["DecisionVolume:Enabled"] = "true",
            ["OrderExecution:BaseUrl"] = "http://order-execution",
        });
        _ = factory.CreateClient();

        factory.Services.GetService<OrderExecutionGrpcTransport>().Should().BeNull();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IInFlightCloseProvider>().Should().BeOfType<HttpInFlightCloseProvider>();
        DailyBarsComposition.Source(scope.ServiceProvider, factory.Services.GetRequiredService<IConfiguration>())
            .Should().BeOfType<HttpDailyBarsSource>();
    }

    // 共有の選択（判断の共有口と Stage 0 の基底が同じものを使う）: 宣言があれば gRPC、BaseUrl だけなら REST、どちらも無ければ null。
    // Stage 0 の基底の「日足の口がある」も宣言で満たす。
    [Fact]
    public void T_10_2909_日足の取得元の選択と_Stage0_の基底の口()
    {
        using (var grpc = new Factory(new() { ["OrderExecution:Grpc"] = "http://order-execution-service:8081" }))
        {
            _ = grpc.CreateClient();
            using var scope = grpc.Services.CreateScope();
            var config = grpc.Services.GetRequiredService<IConfiguration>();
            DailyBarsComposition.Source(scope.ServiceProvider, config).Should().BeOfType<GrpcDailyBarsSource>();
            scope.ServiceProvider.GetRequiredService<IInFlightCloseProvider>().Should().BeOfType<GrpcInFlightCloseProvider>(
                "宣言だけで（BaseUrl が空でも）読む");
        }

        using var none = new Factory([]);
        _ = none.CreateClient();
        using var noneScope = none.Services.CreateScope();
        DailyBarsComposition.Source(noneScope.ServiceProvider, none.Services.GetRequiredService<IConfiguration>()).Should().BeNull();
        noneScope.ServiceProvider.GetRequiredService<IInFlightCloseProvider>().Should().BeOfType<NoOpInFlightCloseProvider>();

        Stage0AsOfComposition.HasBaseEndpoints(Config(new() { ["Reports:Grpc"] = "http://r:8081", ["OrderExecution:Grpc"] = "http://o:8081" }))
            .Should().BeTrue();
        Stage0AsOfComposition.HasBaseEndpoints(Config(new() { ["Reports:BaseUrl"] = "http://r", ["OrderExecution:Grpc"] = "http://o:8081" }))
            .Should().BeTrue();
        Stage0AsOfComposition.HasBaseEndpoints(Config(new() { ["Reports:Grpc"] = "http://r:8081" }))
            .Should().BeFalse("日足の口が無ければ基底は組まない");
    }

    // 陰性対照: 宣言してあるのに使えない宛先は起動時に落とす（黙って REST へ戻さない）。
    [Theory]
    [InlineData("https://order-execution-service:8081")]
    [InlineData("order-execution-service:8081")]
    public void T_10_2909_使えない宛先は起動時に落とす(string address)
    {
        using var factory = new Factory(new() { ["OrderExecution:Grpc"] = address });

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*OrderExecution:Grpc*");
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class Factory(Dictionary<string, string> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            foreach (var (k, v) in settings)
                builder.UseSetting(k, v);
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = "amqp://localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
            }));
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
            // NFR, FR-10, FR-19, IADR-0561 決定 3: 閉場前の手仕舞いの常駐が実時刻の窓で偽の提供側を照会しないよう、時計を休場の時刻へ固定する（TestHostClock）。
            builder.ConfigureTestServices(services => services.UseFixedClock(TestHostClock.OutsideMarketHours));
        }
    }
}
