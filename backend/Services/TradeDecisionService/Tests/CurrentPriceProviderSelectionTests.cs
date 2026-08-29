using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
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

// FR-02, #158, IADR-0068/0099: 現在値供給は常に MarketDataCurrentPriceProvider として登録され、実接続の可否は
// MarketData:Provider（共有 MarketDataSourceFactory）で決まる。未設定なら no-op（IsEnabled=false・現在値なし＝現行挙動）。
public class CurrentPriceProviderSelectionTests
{
    [Fact]
    public void 現在値供給はMarketDataCurrentPriceProviderとして登録される()
    {
        using var factory = new Factory(provider: null, apiKey: null);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICurrentPriceProvider>()
            .Should().BeOfType<MarketDataCurrentPriceProvider>();
    }

    [Fact]
    public async Task Provider未設定はIsEnabledがfalseで現在値なし_現行動作()
    {
        using var factory = new Factory(provider: null, apiKey: null);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<ICurrentPriceProvider>();

        provider.IsEnabled.Should().BeFalse();
        (await provider.GetCurrentPriceAsync(DecisionTrigger.Scheduled("AAPL", Market.UnitedStates)))
            .Should().BeNull();
    }

    [Fact]
    public void Provider_finnhubかつAPIキーありならIsEnabledがtrue()
    {
        using var factory = new Factory(provider: "finnhub", apiKey: "test-key");
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICurrentPriceProvider>()
            .IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Provider_finnhubでもAPIキー無しはno_op_IsEnabledがfalse()
    {
        // 構成不備（キー無し）は起動失敗させず no-op へ倒す（IADR-0068 決定6）。有効化されていないため発注抑止ゲートも効かない。
        using var factory = new Factory(provider: "finnhub", apiKey: null);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICurrentPriceProvider>()
            .IsEnabled.Should().BeFalse();
    }

    private sealed class Factory(string? provider, string? apiKey) : WebApplicationFactory<Program>
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
                if (provider is not null)
                    settings["MarketData:Provider"] = provider;
                if (apiKey is not null)
                    settings["MarketData:Finnhub:ApiKey"] = apiKey;
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
