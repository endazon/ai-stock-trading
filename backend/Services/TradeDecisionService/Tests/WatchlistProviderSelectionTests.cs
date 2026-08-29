using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
// IADR-0128: consumer は Infrastructure へ移った。相対名（Composable.Steps.*）参照をテスト本文を触らずに解決する。
using Composable = TradeDecisionService.Infrastructure;

namespace TradeDecisionService.Tests;

// FR-02, FR-13, UC-06, SC-02, IADR-0095: MarketMonitor:BaseUrl の有無で IWatchlistProvider が構成ベース（後方互換・既定 watchlist）/
// 権威源への s2s 同期照会（Http）に切り替わることを検証する。選択は解決時に構成を読む（WebApplicationFactory の構成上書きに追随する）。
public class WatchlistProviderSelectionTests
{
    [Fact]
    public void BaseUrl未設定は構成ベース_後方互換()
    {
        using var factory = new Factory(monitorBaseUrl: null);
        _ = factory.CreateClient(); // ホスト起動

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IWatchlistProvider>().Should().BeOfType<ConfigurationWatchlistProvider>();
    }

    [Fact]
    public void BaseUrl設定時は権威源を同期照会する_Http実装()
    {
        using var factory = new Factory(monitorBaseUrl: "http://monitor");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IWatchlistProvider>().Should().BeOfType<HttpWatchlistProvider>();
    }

    private sealed class Factory(string? monitorBaseUrl) : WebApplicationFactory<Program>
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
                if (monitorBaseUrl is not null)
                    settings["MarketMonitor:BaseUrl"] = monitorBaseUrl;
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
