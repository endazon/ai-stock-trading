using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-02, FR-13, IADR-0095, #1030（T-10-1456）: 監視銘柄の照会（名前付き HttpClient "monitor"）は 5 秒で打ち切る
// （定時サイクルを長く塞がない）。値を外す変異が素通りしていたため本番の組み立てで固定する。
public class MonitorClientTimeoutTests
{
    [Fact]
    public void 監視銘柄の照会は5秒で打ち切る()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("monitor").Timeout
            .Should().Be(TimeSpan.FromSeconds(5));
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = "amqp://localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["MarketMonitor:BaseUrl"] = "http://monitor",
            }));
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
