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

// 🔴 T-10-2971, FR-10, ADR-0009, NFR-04, IADR-0565 決定 2: **本番の組み立て（Program.cs）**で、送る直前の停止状態の照会が
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
