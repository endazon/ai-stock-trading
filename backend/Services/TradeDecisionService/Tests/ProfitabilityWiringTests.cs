using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AwesomeAssertions;
using Wolverine;
using Xunit;
// IADR-0128: consumer は Infrastructure へ移った。相対名（Composable.Steps.*）参照をテスト本文を触らずに解決する。
using Composable = TradeDecisionService.Infrastructure;

namespace TradeDecisionService.Tests;

// FR-17, IADR-0076: 採算費用見積りプロバイダの配線を検証する。Configuration:BaseUrl 未設定（既定）では
// 共有クライアントが既定（未解決）を返すため、アダプタは null（＝採算見積り不能＝安全側 Hold）に倒れる。
public class ProfitabilityWiringTests(ProfitabilityWiringTests.Factory factory)
    : IClassFixture<ProfitabilityWiringTests.Factory>
{
    [Fact]
    public void 採算プロバイダはアダプタとして解決する()
    {
        using var scope = factory.Services.CreateScope();

        var provider = scope.ServiceProvider.GetRequiredService<IProfitabilityAssumptionsProvider>();

        provider.Should().BeOfType<AssumptionsProfitabilityProvider>();
    }

    [Fact]
    public async Task BaseUrl未設定では未解決でnullを返す()
    {
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IProfitabilityAssumptionsProvider>();

        var assessment = await provider.AssessAsync(Market.UnitedStates, notional: 20_000m);

        // Configuration:BaseUrl 未設定 → DefaultAssumptionsProvider（未解決）→ アダプタは null（安全側）。
        assessment.Should().BeNull();
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RabbitMq:ConnectionString"] = "amqp://localhost",
                    ["Otlp:Endpoint"] = "http://localhost:4317",
                }));
            builder.ConfigureServices(services =>
            {
                // ADR-0013, IADR-0129, #354: 実 RabbitMQ を避けて Wolverine の外部トランスポートを無効化する
                // （ハンドラの発見は Program.cs 側の配線が担う）。
                services.DisableAllExternalWolverineTransports();
            });
        }
    }
}
