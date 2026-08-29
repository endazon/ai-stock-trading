using System.Net;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06/07: 報告書サービスホストが起動し、ヘルスが応答することを検証する。
public class HealthEndpointTests(ReportWorkerWebApplicationFactory factory)
    : IClassFixture<ReportWorkerWebApplicationFactory>
{
    [Fact]
    public async Task ヘルスチェック_live_は応答する()
    {
        var res = await factory.CreateClient().GetAsync("/health/live");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
