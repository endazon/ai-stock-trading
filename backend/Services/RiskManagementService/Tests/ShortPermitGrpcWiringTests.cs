using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.ExternalServices;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.OrderExecution.V1;

namespace RiskManagementService.Tests;

// T-10-2906, NFR, FR-10, UC-06, MSP:ADR-0029, IADR-0559 決定 3, #1391 (#753):
// **本番の Program.cs の組み立て**で、`OrderExecution:Grpc` の有無が借株可否の供給元を切り替え、**既定は REST** であることを固定する。
// 🔴 型だけでなく、組み立てた実装で実際に照会し、偽の提供側の `GetShortPermit` が呼んだ分だけ増えることまで見る。
public class ShortPermitGrpcWiringTests
{
    [Fact]
    public async Task T_10_2906_宣言すれば借株可否を_gRPC_で読み_REST_の宛先より優先する()
    {
        var behavior = new OrderExecutionReadStubBehavior
        {
            ShortPermit = OrderExecutionReadStubBehavior.Returns(new Proto.GetShortPermitResponse
            {
                Symbol = "MSFT",
                Market = Proto.Market.UnitedStates,
                Status = Proto.ShortPermitState.NotPermitted,
            }),
        };
        await using var host = await OrderExecutionReadStubHost.StartAsync(behavior);
        await using var factory = new RiskWorkerWebApplicationFactory
        {
            HostSettings =
            {
                ["OrderExecution:Grpc"] = host.Address,
                // 🔴 REST の宛先を置いても gRPC が優先されること。
                ["OrderExecution:BaseUrl"] = "http://order-execution-rest-must-not-be-used",
            },
        };
        _ = factory.CreateClient();

        factory.Services.GetService<OrderExecutionGrpcTransport>().Should().NotBeNull();
        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<IShortSellBorrowSource>();
        source.Should().BeOfType<GrpcShortSellBorrowSource>();

        var observation = await source.GetAsync("MSFT", Market.UnitedStates, TestContext.Current.CancellationToken);

        observation.ShortPermit.Should().BeFalse("組み立てた読み手が偽の提供側の答え（借りられない）を読んだ");
        behavior.ShortPermitCalls.Should().Be(1);
    }

    // 宣言だけで（BaseUrl が空でも）読む。
    [Fact]
    public async Task T_10_2906_宣言があれば_BaseUrl_が空でも照会する()
    {
        await using var factory = new RiskWorkerWebApplicationFactory
        {
            HostSettings = { ["OrderExecution:Grpc"] = "http://order-execution-service:8081" },
        };
        _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IShortSellBorrowSource>().Should().BeOfType<GrpcShortSellBorrowSource>();
    }

    // 陰性対照: 宣言が無ければ輸送そのものが登録されず、従来どおり（BaseUrl があれば REST、無ければ「分からない」）。
    [Theory]
    [InlineData("http://order-execution", typeof(HttpShortSellBorrowSource))]
    [InlineData(null, typeof(UnavailableShortSellBorrowSource))]
    public async Task T_10_2906_宣言が無ければ従来どおり(string? baseUrl, Type expected)
    {
        await using var factory = new RiskWorkerWebApplicationFactory
        {
            HostSettings = { ["OrderExecution:BaseUrl"] = baseUrl },
        };
        _ = factory.CreateClient();

        factory.Services.GetService<OrderExecutionGrpcTransport>().Should().BeNull();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IShortSellBorrowSource>().Should().BeOfType(expected);
    }

    // 陰性対照: 宣言してあるのに使えない宛先は起動時に落とす（黙って REST へ戻さない）。
    [Theory]
    [InlineData("https://order-execution-service:8081")]
    [InlineData("order-execution-service:8081")]
    public async Task T_10_2906_使えない宛先は起動時に落とす(string address)
    {
        await using var factory = new RiskWorkerWebApplicationFactory
        {
            HostSettings = { ["OrderExecution:Grpc"] = address },
        };

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*OrderExecution:Grpc*");
    }
}
