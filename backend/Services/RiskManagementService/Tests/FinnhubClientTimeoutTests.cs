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

    // NFR, #1532, IADR-0614: 外部の情報源・市場データ・為替を取る HttpClient は応答の本文に上限を持つ（既定はほぼ無制限）。
    [Theory]
    [InlineData("marketdata")]
    [InlineData("fx")]
    public void 時価評価と為替の照会は応答の大きさに上限を持つ(string name)
    {
        using var factory = new RiskWorkerWebApplicationFactory();

        factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(name).MaxResponseContentBufferSize
            .Should().Be(AiStockTrading.Shared.Infrastructure.Composable.ExternalHttpResponseLimits.MaxResponseBytes);
    }
}
