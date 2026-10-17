using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
using AwesomeAssertions;
using BacktestService.Features.Backtest;
using BacktestService.Infrastructure.ExternalServices;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.OrderExecution.V1;

namespace BacktestService.Tests;

// T-10-2911, NFR, FR-15, MSP:ADR-0029, IADR-0528 決定8, IADR-0559 決定 3, #1391 (#753):
// **本番の Program.cs の組み立て**で、`OrderExecution:Grpc` の有無が過去データ源 `order-execution` の実装を切り替え、**既定は REST** であることを固定する。
// 🔴 型だけでなく、組み立てた源で実際に読み込み、偽の提供側の `GetDailyBars` が呼ばれることまで見る。自己申告（introspection）も実際の選択と一致させる。
public class OrderExecutionGrpcWiringTests
{
    [Fact]
    public async Task T_10_2911_宣言すれば過去データ源は_gRPC_で読み_BaseUrl_が無くても選ばれる()
    {
        var behavior = new OrderExecutionReadStubBehavior
        {
            DailyBars = OrderExecutionReadStubBehavior.Returns(new Proto.GetDailyBarsResponse
            {
                Symbol = "AAPL",
                Market = Proto.Market.UnitedStates,
                Status = Proto.DailyBarsState.Available,
                Bars = { new Proto.DailyBarRow { Date = "2026-09-15", Open = "1", High = "2", Low = "0.5", Close = "1.5", Volume = 10 } },
            }),
        };
        await using var host = await OrderExecutionReadStubHost.StartAsync(behavior);
        using var factory = new BacktestWorkerWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Backtest:BarData:Provider"] = "order-execution",
            ["OrderExecution:Grpc"] = host.Address,
        });

        var source = factory.Services.GetRequiredService<IHistoricalBarSource>();
        source.Should().BeOfType<GrpcOrderExecutionHistoricalBarSource>();
        var load = await source.LoadBarsAsync(
            [("AAPL", Market.UnitedStates)], new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 16), TestContext.Current.CancellationToken);

        load.Gaps.Should().BeEmpty();
        load.Bars.Should().ContainSingle().Which.Close.Should().Be(1.5m);
        behavior.DailyBarsCalls.Should().Be(1, "組み立てた源が実際に偽の提供側を呼んだ");
        var dto = await factory.CreateClient().GetFromJsonAsync<ServiceIntrospectionDto>(
            IntrospectionExtensions.IntrospectionPath, TestContext.Current.CancellationToken);
        dto!.Ports.Should().ContainSingle(p => p.Port == "historical-bar-data" && p.Implementation == "order-execution");
    }

    // 宣言は BaseUrl より優先する。
    [Fact]
    public void T_10_2911_宣言は_BaseUrl_より優先する()
    {
        using var factory = new BacktestWorkerWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Backtest:BarData:Provider"] = "order-execution",
            ["Backtest:BarData:OrderExecution:BaseUrl"] = "http://order-execution-rest-must-not-be-used:8080",
            ["OrderExecution:Grpc"] = "http://order-execution-service:8081",
        });

        factory.Services.GetRequiredService<IHistoricalBarSource>().Should().BeOfType<GrpcOrderExecutionHistoricalBarSource>();
    }

    // 陰性対照 1: 宣言が無ければ輸送そのものが登録されず、従来どおり REST。
    [Fact]
    public void T_10_2911_宣言が無ければ_REST_のまま()
    {
        using var factory = new BacktestWorkerWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Backtest:BarData:Provider"] = "order-execution",
            ["Backtest:BarData:OrderExecution:BaseUrl"] = "http://order-execution-service:8080",
        });

        factory.Services.GetService<OrderExecutionGrpcTransport>().Should().BeNull();
        factory.Services.GetRequiredService<IHistoricalBarSource>().Should().BeOfType<HttpOrderExecutionHistoricalBarSource>();
    }

    // 陰性対照 2: 宣言だけでは過去データ源を選ばない（provider の allow-list を崩さない）。
    [Fact]
    public void T_10_2911_宣言だけでは過去データ源を選ばない()
    {
        using var factory = new BacktestWorkerWebApplicationFactory(new Dictionary<string, string?>
        {
            ["OrderExecution:Grpc"] = "http://order-execution-service:8081",
        });

        factory.Services.GetRequiredService<IHistoricalBarSource>().Should().BeOfType<NoOpHistoricalBarSource>();
    }

    // 陰性対照 3: 宣言してあるのに使えない宛先は起動時に落とす（黙って REST へ戻さない）。
    [Theory]
    [InlineData("https://order-execution-service:8081")]
    [InlineData("order-execution-service:8081")]
    public void T_10_2911_使えない宛先は起動時に落とす(string address)
    {
        using var factory = new BacktestWorkerWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Backtest:BarData:Provider"] = "order-execution",
            ["OrderExecution:Grpc"] = address,
        });

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*OrderExecution:Grpc*");
    }
}
