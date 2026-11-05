using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ReportService.Tests;

// FR-16, #1133, IADR-0469（T-10-1863）: 報告書の評価損益の現在値（名前付き HttpClient "marketdata"＝Finnhub /quote）は
// 5 秒で打ち切る（超えたら前回値へ倒れる。既定の 100 秒は待たない）。本番の組み立てで固定する。
public class FinnhubClientTimeoutTests(ReportWorkerWebApplicationFactory factory)
    : IClassFixture<ReportWorkerWebApplicationFactory>
{
    [Fact]
    public void T_10_1863_報告書の現在値の照会は5秒で打ち切る()
    {
        factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("marketdata").Timeout
            .Should().Be(FinnhubHttpTimeouts.Quote);
    }

    // NFR, #1532, IADR-0614: 外部の情報源・市場データ・為替を取る HttpClient は応答の本文に上限を持つ（既定はほぼ無制限）。
    [Theory]
    [InlineData("marketdata")]
    [InlineData("fx")]
    public void 報告書の現在値と為替の照会は応答の大きさに上限を持つ(string name)
    {
        factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(name).MaxResponseContentBufferSize
            .Should().Be(AiStockTrading.Shared.Infrastructure.Composable.ExternalHttpResponseLimits.MaxResponseBytes);
    }
}
