using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Infrastructure.Composable.SecondFactor;
using RiskManagementService.Features.RiskManagement;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// NFR-06, FR-14, #1542, IADR-0615: リスク管理の危険な操作（kill switch の解除・再開・段階の昇格・GFV 停止の解除・乖離の取り込み）は
// 第 2 要素（TOTP）を提供側で確かめる。止める向き（kill switch の起動・一時停止）には課さない。段 1（Audit）は記録だけ。
public class OwnerSecondFactorWiringTests
{
    private const string Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    private static RiskWorkerWebApplicationFactory Factory(string? mode) => new()
    {
        HostSettings = mode is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>
            {
                [$"{OwnerSecondFactorOptions.SectionName}:Mode"] = mode,
                [$"{OwnerSecondFactorOptions.SectionName}:Secret"] = Secret,
            },
    };

    private static HttpClient Owner(RiskWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    private static string CurrentCode()
    {
        Totp.TryDecodeBase32(Secret, out var key);
        return Totp.Compute(key, Totp.StepOf(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Enforceではコードの無い解除と再開を403で拒み_起動と一時停止は通す()
    {
        using var factory = Factory("Enforce");
        var client = Owner(factory);

        (await client.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("止める"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/risk-controls/pause", new PauseRequest("止める"))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.PostAsJsonAsync("/risk-controls/kill-switch/disengage", new KillSwitchRequest("再開"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/risk-controls/resume", new PauseRequest("再開"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetFromJsonAsync<KillSwitchState>("/risk-controls/kill-switch"))!.Engaged.Should().BeTrue("拒んだら何も変えない");
    }

    [Fact]
    public async Task Enforceでも正しいコードがあれば解除する_同じコードの2回目は拒む()
    {
        using var factory = Factory("Enforce");
        var client = Owner(factory);
        await client.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("止める"));
        var code = CurrentCode();

        (await client.PostAsJsonAsync("/risk-controls/kill-switch/disengage", new KillSwitchRequest("再開", code))).StatusCode.Should().Be(HttpStatusCode.OK);
        await client.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("止める"));
        (await client.PostAsJsonAsync("/risk-controls/kill-switch/disengage", new KillSwitchRequest("再開", code))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Audit")]
    public async Task 既定と段1はコードが無くても解除する(string? mode)
    {
        using var factory = Factory(mode);
        var client = Owner(factory);
        await client.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("止める"));

        (await client.PostAsJsonAsync("/risk-controls/kill-switch/disengage", new KillSwitchRequest("再開"))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Enforceではコードの無いGFV停止の解除と乖離の取り込みを403で拒む()
    {
        using var factory = Factory("Enforce");
        var client = Owner(factory);

        (await client.PostAsJsonAsync("/risk-controls/good-faith-violations/clear", new { Reason = "是正済み" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/risk-controls/position-drift/adopt", new { Symbol = "AAPL", Market = 1, Reason = "台帳を合わせる" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Enforceではコードの無い昇格を403で拒む()
    {
        using var factory = Factory("Enforce");
        var client = Owner(factory);

        (await client.PostAsJsonAsync("/risk-controls/stage-gate/transition", new { TargetStage = 1 })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
