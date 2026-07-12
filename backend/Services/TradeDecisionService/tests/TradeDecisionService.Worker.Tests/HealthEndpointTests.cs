using System.Net;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.TradeDecision.Worker.Tests;

// FR-04: ホストが起動し（安全既定のプレースホルダで）ヘルスエンドポイントが応答することを検証する。
public class HealthEndpointTests(HealthEndpointTests.Factory factory) : IClassFixture<HealthEndpointTests.Factory>
{
    [Fact]
    public async Task ヘルスチェック_live_は応答する()
    {
        var res = await factory.CreateClient().GetAsync("/health/live");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RabbitMq:ConnectionString"] = "amqp://localhost",
                    ["Otlp:Endpoint"] = "http://localhost:4317",
                }));
            builder.ConfigureServices(services =>
            {
                // 実 RabbitMQ を避けてテストハーネスへ差し替える。
                services.RemoveAll<IBusControl>();
                services.AddMassTransitTestHarness(x => x.AddConsumer<Composable.Steps.PriceMovementDetectedConsumer>());
            });
        }
    }
}
