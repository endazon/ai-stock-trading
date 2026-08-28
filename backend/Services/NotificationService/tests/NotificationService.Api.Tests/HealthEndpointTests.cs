using System.Net;
using AwesomeAssertions;
using Xunit;

namespace NotificationService.Api.Tests;

// FR-09: 通知サービスホストが起動し（既定の安全 sender で外部送信せず）、ヘルスが応答することを検証する。
public class HealthEndpointTests(NotificationWorkerWebApplicationFactory factory)
    : IClassFixture<NotificationWorkerWebApplicationFactory>
{
    [Fact]
    public async Task ヘルスチェック_live_は応答する()
    {
        var res = await factory.CreateClient().GetAsync("/health/live");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
