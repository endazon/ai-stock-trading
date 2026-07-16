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

    // #11, IADR-0062 決定2: タイムアウトは LlmGateway:TimeoutSeconds（秒）。
    // fail-safe: 未設定・不正・非正値は既定 30 秒（＝従来値）。無限待ちや 0 秒には倒さない。
    [Theory]
    [InlineData(null, 30)]      // 未設定
    [InlineData("", 30)]        // 空
    [InlineData("abc", 30)]     // 非数値
    [InlineData("0", 30)]       // 0 秒（即時タイムアウト）は既定へ
    [InlineData("-5", 30)]      // 負値は既定へ
    [InlineData("90", 90)]      // 明示設定は反映
    public void TimeoutSeconds_は不正値を既定30秒へ倒す(string? configured, int expectedSeconds)
    {
        using var factory = new Factory(llmGatewayBaseUrl: "http://llm-gateway", timeoutSeconds: configured);
        _ = factory.CreateClient();

        var http = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("llm");
        http.Timeout.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    private sealed class Factory(string? llmGatewayBaseUrl, string? timeoutSeconds = null) : WebApplicationFactory<Program>
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
                if (timeoutSeconds is not null)
                    settings["LlmGateway:TimeoutSeconds"] = timeoutSeconds;
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
