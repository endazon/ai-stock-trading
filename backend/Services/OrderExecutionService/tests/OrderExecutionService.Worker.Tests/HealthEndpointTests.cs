using System.Net;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.OrderExecution.Worker.Tests;

// FR-05: ホストが起動し（既定の paper ブローカで安全に）ヘルスエンドポイントが応答することを検証する。
public class HealthEndpointTests(ExecutionWorkerWebApplicationFactory factory)
    : IClassFixture<ExecutionWorkerWebApplicationFactory>
{
    [Fact]
    public async Task ヘルスチェック_live_は応答する()
    {
        var res = await factory.CreateClient().GetAsync("/health/live");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
