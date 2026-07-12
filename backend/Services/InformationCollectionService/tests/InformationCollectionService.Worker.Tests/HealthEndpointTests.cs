using System.Net;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.InformationCollection.Worker.Tests;

// FR-01: 情報収集サービスホストが起動し（既定の安全な no-op 情報源で外部接続せず）、ヘルスが応答することを検証する。
public class HealthEndpointTests(InformationCollectionWorkerWebApplicationFactory factory)
    : IClassFixture<InformationCollectionWorkerWebApplicationFactory>
{
    [Fact]
    public async Task ヘルスチェック_live_は応答する()
    {
        var res = await factory.CreateClient().GetAsync("/health/live");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
