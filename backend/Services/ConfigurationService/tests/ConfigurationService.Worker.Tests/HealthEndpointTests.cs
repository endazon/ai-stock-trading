using System.Net;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Configuration.Worker.Tests;

// FR-17: 設定管理サービスホストが起動し、ヘルスが応答することを検証する。
public class HealthEndpointTests(ConfigurationWorkerWebApplicationFactory factory)
    : IClassFixture<ConfigurationWorkerWebApplicationFactory>
{
    [Fact]
    public async Task ヘルスチェック_live_は応答する()
    {
        var res = await factory.CreateClient().GetAsync("/health/live");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
