using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-02, FR-10, #1133, IADR-0469（T-10-1863）: 判断の現在値の照会（名前付き HttpClient "marketdata"＝Finnhub /quote）は
// 5 秒で打ち切る。既定の 100 秒のままだと、TLS ハンドシェイクが止まる相手に 1 判断が最大 100 秒待つ。本番の組み立てで固定する。
public class FinnhubClientTimeoutTests
{
    [Fact]
    public void T_10_1863_判断の現在値の照会は5秒で打ち切る()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("marketdata").Timeout
            .Should().Be(FinnhubHttpTimeouts.Quote);
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
            }));
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
