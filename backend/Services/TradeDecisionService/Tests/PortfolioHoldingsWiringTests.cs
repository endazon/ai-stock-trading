using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// T-10-2943, FR-04, FR-10, #1397, IADR-0562 決定 3: 取引判断サービスの**実際の DI 構成**が、判断のサービスへ保有中の全銘柄の供給口
// （保有照会と同じ実装の IHeldSymbolsProvider）を渡すこと（渡し忘れるとプロンプトの節が黙って消える）。
public class PortfolioHoldingsWiringTests
{
    [Fact]
    public void T_10_2943_本番構成は判断のサービスへ保有中の全銘柄の供給口を渡す()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var app = scope.ServiceProvider.GetRequiredService<TradeDecisionAppService>();
        typeof(TradeDecisionAppService).GetField("_holdings", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(app).Should().BeAssignableTo<IHeldSymbolsProvider>().And.NotBeNull();
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
