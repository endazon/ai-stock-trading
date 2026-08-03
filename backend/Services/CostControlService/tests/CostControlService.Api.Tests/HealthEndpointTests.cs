using System.Net;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.CostControl.Api.Tests;

// NFR（費用）: 費用統制サービスホストが起動し、ヘルスが応答することを検証する。
public class HealthEndpointTests(CostControlWorkerWebApplicationFactory factory)
    : IClassFixture<CostControlWorkerWebApplicationFactory>
{
    [Fact]
    public async Task ヘルスチェック_live_は応答する()
    {
        var res = await factory.CreateClient().GetAsync("/health/live");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
