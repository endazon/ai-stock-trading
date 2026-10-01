using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace AuditService.Tests;

// NFR, IADR-0468, #1137: readiness の DB 疎通チェック（npgsql・"ready" タグ）の打ち切りを、本番の組み立て（Program.cs）から
// 読んで固定する。未指定（null）だと打ち切りは Npgsql の既定（接続 15 秒）に委ねられ、readinessProbe の timeoutSeconds が
// 先に切る（2026-09-30 17:49 UTC の "completed after 1002ms … The operation was canceled"）。
// 打ち切りが probe の timeoutSeconds より厳密に短いことは PlatformShim.Tests の ReadinessProbeTimeoutConsistencyTests が固定する。
public class DbReadinessHealthCheckTimeoutTests(AuditWorkerWebApplicationFactory factory)
    : IClassFixture<AuditWorkerWebApplicationFactory>
{
    [Fact]
    public void DB疎通チェックはreadyタグで登録され打ち切りが共通の3秒に固定されている()
    {
        var registrations = factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        var npgsql = registrations.Should().ContainSingle(r => r.Name == "npgsql").Subject;
        npgsql.Tags.Should().Contain("ready");
        npgsql.Timeout.Should().Be(HealthCheckExtensions.NpgSqlReadinessTimeout);
        npgsql.Timeout.Should().Be(TimeSpan.FromSeconds(3));
    }
}
