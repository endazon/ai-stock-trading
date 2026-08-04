using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Application.State;
using AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;
using AiStockTrading.Shared.Contracts.Trading;
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

// FR-08, IADR-0069/0072: RAG 取得は常に KnowledgeBaseRetrievalContextProvider として登録され、実接続の可否は
// KnowledgeBase:Search:BaseUrl（#18 の IKnowledgeBaseSearch）で決まる。未設定なら NoOp 検索＝空取得＝文脈なし（安全既定）。
public class RetrievalContextProviderSelectionTests
{
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 7, 10), "米国株の押し目買い方針");

    [Fact]
    public void 取得ポートはKnowledgeBaseRetrievalContextProviderとして登録される()
    {
        using var factory = new Factory(searchBaseUrl: null);
        _ = factory.CreateClient(); // ホスト起動

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IRetrievalContextProvider>()
            .Should().BeOfType<KnowledgeBaseRetrievalContextProvider>();
    }

    [Fact]
    public async Task Search_BaseUrl未設定は空取得_文脈なしの現行動作()
    {
        using var factory = new Factory(searchBaseUrl: null);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IRetrievalContextProvider>();

        var result = await provider.GetContextAsync(DecisionTrigger.Scheduled("AAPL", Market.UnitedStates), Policy);

        result.Should().BeEmpty();
    }

    private sealed class Factory(string? searchBaseUrl) : WebApplicationFactory<Program>
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
                if (searchBaseUrl is not null)
                    settings["KnowledgeBase:Search:BaseUrl"] = searchBaseUrl;
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
