using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
// IADR-0128: consumer は Infrastructure へ移った。相対名（Composable.Steps.*）参照をテスト本文を触らずに解決する。
using Composable = AiStockTrading.TradeDecision.Infrastructure.Composable;

namespace AiStockTrading.TradeDecision.Api.Tests;

// FR-07, IADR-0028: Reports:BaseUrl の有無で IDailyPolicyProvider が安全既定（プレースホルダ）/ 同期照会（Http）に切り替わることを検証する。
public class DailyPolicyProviderSelectionTests
{
    [Fact]
    public void BaseUrl未設定は安全既定のプレースホルダ()
    {
        using var factory = new Factory(reportsBaseUrl: null);
        _ = factory.CreateClient(); // ホスト起動

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IDailyPolicyProvider>().Should().BeOfType<PlaceholderDailyPolicyProvider>();
    }

    [Fact]
    public void BaseUrl設定時は報告書サービスを同期照会する_Http実装()
    {
        using var factory = new Factory(reportsBaseUrl: "http://reports");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IDailyPolicyProvider>().Should().BeOfType<HttpDailyPolicyProvider>();
    }

    private sealed class Factory(string? reportsBaseUrl) : WebApplicationFactory<Program>
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
                if (reportsBaseUrl is not null)
                    settings["Reports:BaseUrl"] = reportsBaseUrl;
                cfg.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services =>
            {
                // ADR-0013, IADR-0129, #354: 実 RabbitMQ を避けて Wolverine の外部トランスポートを無効化する
                // （ハンドラの発見は Program.cs 側の配線が担う）。
                services.DisableAllExternalWolverineTransports();
            });
        }
    }
}
