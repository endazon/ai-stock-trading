using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Worker.Composable.Adapters;
using FluentAssertions;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AiStockTrading.TradeDecision.Worker.Tests;

// #79, FR-04, IADR-0017: LlmGateway:BaseUrl の有無で ILlmCompletionClient が安全既定（プレースホルダ＝常に Hold）/
// 実 egress（Http）に切り替わることを検証する（fail-safe: 未設定なら実 LLM を呼ばない）。
public class LlmCompletionClientSelectionTests
{
    [Fact]
    public void BaseUrl未設定は安全既定のプレースホルダ()
    {
        using var factory = new Factory(llmGatewayBaseUrl: null);
        _ = factory.CreateClient(); // ホスト起動

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ILlmCompletionClient>().Should().BeOfType<PlaceholderLlmCompletionClient>();
    }

    [Fact]
    public void BaseUrl設定時は_LLM_ゲートウェイを呼ぶ_Http実装()
    {
        using var factory = new Factory(llmGatewayBaseUrl: "http://llm-gateway");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ILlmCompletionClient>().Should().BeOfType<HttpLlmCompletionClient>();
    }

    private sealed class Factory(string? llmGatewayBaseUrl) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["RabbitMq:ConnectionString"] = "amqp://localhost",
                    ["Otlp:Endpoint"] = "http://localhost:4317",
                };
                if (llmGatewayBaseUrl is not null)
                    settings["LlmGateway:BaseUrl"] = llmGatewayBaseUrl;
                cfg.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBusControl>();
                services.AddMassTransitTestHarness(x => x.AddConsumer<Composable.Steps.PriceMovementDetectedConsumer>());
            });
        }
    }
}
