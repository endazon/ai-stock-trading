using AiStockTrading.TestSupport.PlatformShim.Foundation.Tls;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// NFR, NFR-18, #1535, IADR-0621 決定5: LLM ゲートウェイの HttpClient（llm）だけが基盤の CA を信頼する専用の検証を持つ。
// 外部 API（Finnhub ほか）の HttpClient は既定のハンドラのまま（システムの信頼ストア）。
public class LlmGatewayInfraCaTrustWiringTests
{
    [Fact]
    public void llmの主ハンドラは基盤のCAの検証を持ち外部APIのハンドラは持たない()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();
        var handlers = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>();

        PlatformInfraCaTrust.IsPlatformInfraCaHandler(Primary(handlers.CreateHandler("llm"))).Should().BeTrue();
        foreach (var external in new[] { "marketdata", "fx" })
            PlatformInfraCaTrust.IsPlatformInfraCaHandler(Primary(handlers.CreateHandler(external))).Should().BeFalse(external);
    }

    private static HttpMessageHandler Primary(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler d && d.InnerHandler is not null)
            handler = d.InnerHandler;
        return handler;
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RabbitMq:ConnectionString"] = "amqp://localhost",
                    ["Otlp:Endpoint"] = "http://localhost:4317",
                    [PlatformInfraCaTrust.PathKey] = "/etc/platform-infra-ca/ca.crt",
                }));
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
