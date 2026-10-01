using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, #1133, IADR-0469（T-10-1863）: 時価評価の補充（名前付き HttpClient "marketdata"＝Finnhub /quote）は 5 秒で打ち切る
// （補充の巡回は既定 60 秒。既定の 100 秒のままだと 1 銘柄で巡回を越える）。本番の組み立てで固定する。
public class FinnhubClientTimeoutTests
{
    [Fact]
    public void T_10_1863_時価評価の補充の照会は5秒で打ち切る()
    {
        using var factory = new RiskWorkerWebApplicationFactory();

        factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("marketdata").Timeout
            .Should().Be(FinnhubHttpTimeouts.Quote);
    }
}
