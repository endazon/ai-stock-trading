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
using Proto = AiStockTrading.Shared.Grpc.CostControl.V1;

namespace TradeDecisionService.Tests;

// 🔴 T-10-3086, NFR-13, MSP:ADR-0029, IADR-0284 決定 5（段 5 の追補 3）, IADR-0604, #1510 (#753): **本番の Program.cs の組み立て**で、
// `CostControl:Grpc` の有無が LLM 費用の停止の関所を切り替え、**既定は REST** であることを固定する。組み立てた関所で実際に呼び、
// 偽の提供側の呼ばれた回数が増えることまで見る（型だけでは配線を外しても緑のまま、を捕まえられない）。
public class CostControlGrpcWiringTests
{
    [Fact]
    public async Task T_10_3086_宣言すれば_gRPC_で読み_REST_の宛先より優先する()
    {
        var behavior = CostStateStubBehavior.Returns(new Proto.GetCostStateResponse { IsHalted = true, IntervalMultiplier = "0" });
        await using var host = await CostStateGrpcStubHost.StartAsync(behavior);
        using var factory = new Factory(new()
        {
            ["CostControl:Grpc"] = host.Address,
            // 🔴 REST の宛先を置いても gRPC が優先されること。
            ["CostControl:BaseUrl"] = "http://cost-control-rest-must-not-be-used",
        });
        _ = factory.CreateClient();

        factory.Services.GetService<CostControlGrpcTransport>().Should().NotBeNull();
        using var scope = factory.Services.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<ILlmCostHaltGate>();
        gate.Should().BeOfType<GrpcLlmCostHaltGate>();

        var before = behavior.Calls;
        (await gate.IsHaltedAsync(TestContext.Current.CancellationToken)).Should().BeTrue("組み立てた関所が偽の提供側の答えを読んだ");
        (behavior.Calls - before).Should().Be(1);
    }

    // 陰性対照: 宣言が無ければ輸送そのものが登録されず、従来どおり REST（BaseUrl があるとき）／未結線（NoOp）。
    [Fact]
    public void T_10_3086_宣言が無ければ_REST_のまま()
    {
        using (var rest = new Factory(new() { ["CostControl:BaseUrl"] = "http://cost-control-service:8080" }))
        {
            _ = rest.CreateClient();
            rest.Services.GetService<CostControlGrpcTransport>().Should().BeNull();
            using var scope = rest.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ILlmCostHaltGate>().Should().BeOfType<HttpLlmCostHaltGate>();
        }

        using var none = new Factory([]);
        _ = none.CreateClient();
        using var noneScope = none.Services.CreateScope();
        noneScope.ServiceProvider.GetRequiredService<ILlmCostHaltGate>().Should().BeOfType<NoOpLlmCostHaltGate>();
    }

    // 陰性対照: 宣言してあるのに使えない宛先は起動時に落とす（黙って REST へ戻さない）。
    [Theory]
    [InlineData("https://cost-control-service:8081")]
    [InlineData("cost-control-service:8081")]
    public void T_10_3086_使えない宛先は起動時に落とす(string address)
    {
        using var factory = new Factory(new() { ["CostControl:Grpc"] = address });

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*CostControl:Grpc*");
    }

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
            builder.ConfigureTestServices(services => services.UseFixedClock(TestHostClock.OutsideMarketHours));
        }
    }
}
