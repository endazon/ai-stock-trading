using AiStockTrading.Shared.Infrastructure.Composable.Http;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ReportService.Tests;

// NFR, NFR-18, #1535, IADR-0621 決定5: 報告書の LLM ゲートウェイの HttpClient（report-llm）だけが基盤の CA を信頼する専用の検証を持つ。
// 外部 API（Finnhub・為替）の HttpClient は既定のハンドラのまま（システムの信頼ストア）。
public class LlmGatewayInfraCaTrustWiringTests
{
    [Fact]
    public async Task report_llmの主ハンドラは基盤のCAの検証を持ち外部APIのハンドラは持たない()
    {
        await using var factory = new ReportWorkerWebApplicationFactory().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PlatformInfraCaTrust.PathKey] = "/etc/platform-infra-ca/ca.crt",
            })));
        _ = factory.CreateClient();
        var handlers = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>();

        PlatformInfraCaTrust.IsPlatformInfraCaHandler(Primary(handlers.CreateHandler("report-llm"))).Should().BeTrue();
        foreach (var external in new[] { "marketdata", "fx" })
            PlatformInfraCaTrust.IsPlatformInfraCaHandler(Primary(handlers.CreateHandler(external))).Should().BeFalse(external);
    }

    private static HttpMessageHandler Primary(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler d && d.InnerHandler is not null)
            handler = d.InnerHandler;
        return handler;
    }
}
