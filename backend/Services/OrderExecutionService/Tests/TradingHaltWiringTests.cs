extern alias RiskManagementWorker;

using System.Net;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;
using OrderExecutionService.Infrastructure.ExternalServices;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetTradingHalt;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-2971, FR-10, ADR-0009, NFR-04, IADR-0567 決定 2: **本番の組み立て（Program.cs）**で、送る直前の停止状態の照会が
// 発注執行へ渡っていることを固定する（引数は省略可能なので、渡し忘れてもコンパイルも単体の試験も通る）。
//   - 照会先（RiskManagement:BaseUrl）が未構成 → 常に不明 → 新規建ては送られない（fail-closed）。
//   - 構成済み → "risk" HttpClient で照会し、停止中なら送られず、停止していなければ送られる。
public class TradingHaltWiringTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static OrderApproved Entry() => new(
        Guid.NewGuid(),
        new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
            10, 100m, PositionEffect.Open, StopLossPrice: 95m),
        10, DateTimeOffset.UtcNow);

    [Fact]
    public async Task 照会先が未構成なら本番の組み立てで新規建てを送らない()
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITradingHaltSource>().Should().BeOfType<UnconfiguredTradingHaltSource>();
        var result = await scope.ServiceProvider.GetRequiredService<OrderExecutionAppService>().ExecuteAsync(Entry());

        result.Executed.Should().BeNull("停止の状態が不明なら送らない");
        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.TradingHaltStateUnknown);
    }

    [Theory]
    [InlineData(true, false, OrderDispatchForgoneReason.KillSwitchActive)]
    [InlineData(false, true, OrderDispatchForgoneReason.TradingPaused)]
    [InlineData(false, false, null)]
    public async Task 照会先を構成すると本番の組み立てで状態に従う(
        bool killSwitch, bool paused, OrderDispatchForgoneReason? expected)
    {
        // 送り手の本物の型を web 既定で直列化した本文（受け手の契約テストと同じ形）。
        var handler = new StubHandler(JsonSerializer.Serialize(new TradingHaltView(killSwitch, paused), Web));
        await using var factory = new ExecutionWorkerWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RiskManagement:BaseUrl", "http://risk-management-service:8080");
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("risk").ConfigurePrimaryHttpMessageHandler(() => handler));
        });
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITradingHaltSource>().Should().BeOfType<HttpTradingHaltSource>();
        var result = await scope.ServiceProvider.GetRequiredService<OrderExecutionAppService>().ExecuteAsync(Entry());

        handler.Calls.Should().Be(1);
        if (expected is { } reason)
        {
            result.Executed.Should().BeNull();
            result.Forgone!.Reason.Should().Be(reason);
        }
        else
        {
            result.Forgone.Should().BeNull();
            result.Executed.Should().NotBeNull("停止していなければ送る");
        }
    }
}

// 🔴 T-10-3084, NFR, FR-10, MSP:ADR-0029, IADR-0284 決定 5（段 5 の追補 3）, IADR-0604, #1510 (#753): **本番の組み立て**で、
// `RiskManagement:Grpc` の有無が停止の状態の照会口を切り替え、**既定は REST** であることを固定する。宣言があれば REST の宛先より優先し、
// 組み立てた発注の経路が**実際に偽の提供側を呼ぶ**（呼ばれた回数で見る）。使えない宛先は起動時に落とす。
public class TradingHaltGrpcWiringTests
{
    private static OrderApproved Entry() => new(
        Guid.NewGuid(),
        new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
            10, 100m, PositionEffect.Open, StopLossPrice: 95m),
        10, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(true, false, OrderDispatchForgoneReason.KillSwitchActive)]
    [InlineData(false, true, OrderDispatchForgoneReason.TradingPaused)]
    [InlineData(false, false, null)]
    public async Task T_10_3084_宣言すれば_gRPC_で読み_REST_の宛先より優先する(
        bool killSwitch, bool paused, OrderDispatchForgoneReason? expected)
    {
        var behavior = TradingHaltStubBehavior.Returns(
            new AiStockTrading.Shared.Grpc.RiskManagement.V1.GetTradingHaltResponse { KillSwitchEngaged = killSwitch, TradingPaused = paused });
        await using var host = await TradingHaltGrpcStubHost.StartAsync(behavior);
        await using var factory = new ExecutionWorkerWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RiskManagement:Grpc", host.Address);
            // 🔴 REST の宛先を置いても gRPC が優先されること（呼ばれれば名前解決に失敗して不明＝送らない、になる）。
            builder.UseSetting("RiskManagement:BaseUrl", "http://risk-management-rest-must-not-be-used:8080");
        });
        using var scope = factory.Services.CreateScope();

        factory.Services.GetService<RiskManagementGrpcTransport>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ITradingHaltSource>().Should().BeOfType<GrpcTradingHaltSource>();
        var before = behavior.Calls;
        var result = await scope.ServiceProvider.GetRequiredService<OrderExecutionAppService>().ExecuteAsync(Entry());

        (behavior.Calls - before).Should().Be(1, "組み立てた発注の経路が偽の提供側を実際に呼んだ");
        if (expected is { } reason)
        {
            result.Executed.Should().BeNull();
            result.Forgone!.Reason.Should().Be(reason);
        }
        else
        {
            result.Forgone.Should().BeNull();
            result.Executed.Should().NotBeNull("停止していなければ送る");
        }
    }

    // 陰性対照: 宣言が無ければ輸送そのものが登録されず、従来どおり REST（BaseUrl があるとき）。
    [Fact]
    public async Task T_10_3084_宣言が無ければ_REST_のまま()
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("RiskManagement:BaseUrl", "http://risk-management-service:8080"));
        using var scope = factory.Services.CreateScope();

        factory.Services.GetService<RiskManagementGrpcTransport>().Should().BeNull();
        scope.ServiceProvider.GetRequiredService<ITradingHaltSource>().Should().BeOfType<HttpTradingHaltSource>();
    }

    // 陰性対照: 宣言してあるのに使えない宛先は起動時に落とす（黙って REST へ戻さない）。
    [Theory]
    [InlineData("https://risk-management-service:8081")]
    [InlineData("risk-management-service:8081")]
    public async Task T_10_3084_使えない宛先は起動時に落とす(string address)
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("RiskManagement:Grpc", address));

        var act = () => factory.Services;

        act.Should().Throw<InvalidOperationException>().WithMessage("*RiskManagement:Grpc*");
    }
}
