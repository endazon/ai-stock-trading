using System.Net;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Audit.Worker.Tests;

// FR-11: 監査サービスホストが起動し、ヘルスエンドポイントが応答することを検証する。
public class HealthEndpointTests(AuditWorkerWebApplicationFactory factory)
    : IClassFixture<AuditWorkerWebApplicationFactory>
{
    [Fact]
    public async Task ヘルスチェック_live_は応答する()
    {
        var res = await factory.CreateClient().GetAsync("/health/live");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
