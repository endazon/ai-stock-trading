using System.Net;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradeDecisionService.Features.TradeDecision;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, FR-08, ADR-0006, #1525, IADR-0608 決定1: 単一ノードのクラウドの構成（values-cloud）は KB の宛先を持たない。
// 宛先を空にしたまま起動し、検索の取得口が「未構成」を返す（失敗ではない＝判断を止めない）ことを固定する。
// 判断の 1 回（一次と本判断）が検索なしで通ることは RetrievalNotConfiguredInPromptTests が本番の経路で固定する。
public class CloudProfileWithoutKnowledgeBaseTests
{
    [Fact]
    public async Task KBの宛先が空でも起動し検索の取得口は未構成を返す()
    {
        await using var factory = new Factory();

        (await factory.CreateClient().GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IKnowledgeBaseSearch>().GetType().Name.Should().Be("NoOpKnowledgeBaseSearch");
        var result = await scope.ServiceProvider.GetRequiredService<IRetrievalContextProvider>().GetContextWithStatusAsync(
            DecisionTrigger.Scheduled("AAPL", Market.UnitedStates), new DailyPolicy(new DateOnly(2026, 11, 5), "方針"));
        result.Status.Should().Be(RetrievalStatus.NotConfigured);
        result.Contexts.Should().BeEmpty();
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
                    // values-cloud の描画と同じ（宛先なし）。空文字は未設定と同じ no-op。
                    ["KnowledgeBase:Search:BaseUrl"] = "",
                    ["KnowledgeBase:Documents:BaseUrl"] = "",
                }));
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
