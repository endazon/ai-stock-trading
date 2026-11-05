using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 FR-10, FR-14, #1530, #1534, IADR-0613, IADR-0620: SSH の経路の保持（operator hold）と「見た状態」の版の前提条件の組み合わせ。
// - 保持の間の解除は、版の前提条件より先に保持で拒否される（409・code operator_hold）。版が合っていても、古くても、版が無くても（段 2 でも）同じ。
// - 同じ 409 でも本文の code で理由を区別できる（operator_hold と version_conflict）。
// - 保持による起動は前提条件なしの書き込みであり、版を進める。保持より前の版を見た操作は version_conflict になる。
public class KillSwitchHoldAndPreconditionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static RiskWorkerWebApplicationFactory Factory(bool hold, string? precondition = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["RiskManagement:OperatorKillSwitchHold:Enabled"] = hold ? "true" : "false",
            ["RiskManagement:OperatorKillSwitchHold:Reason"] = "SSH からの停止",
        };
        if (precondition is not null)
            settings["RiskControls:KillSwitch:Precondition"] = precondition;
        return new RiskWorkerWebApplicationFactory { HostSettings = settings };
    }

    private static HttpClient Owner(RiskWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    private static async Task<(bool Engaged, string Version)> StateOf(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/risk-controls/kill-switch"));
        return (doc.RootElement.GetProperty("engaged").GetBoolean(), doc.RootElement.GetProperty("version").GetString()!);
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Theory]
    [InlineData("current")]
    [InlineData("stale")]
    [InlineData("none")]
    public async Task 保持の間の解除は版に関わらず保持で拒否され_codeはoperator_hold(string version)
    {
        using var factory = Factory(hold: true);
        var client = Owner(factory);
        var before = await StateOf(client);
        before.Engaged.Should().BeTrue("保持は起動時に kill switch を起動する");

        string? expected = version switch
        {
            "current" => before.Version,
            "stale" => "0",
            _ => null,
        };
        var response = await client.PostAsJsonAsync(
            "/risk-controls/kill-switch/disengage", new KillSwitchRequest("再開", ExpectedVersion: expected));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeOf(response)).Should().Be(KillSwitchHeldByOperatorException.Code);
        (await StateOf(client)).Should().Be(before, "拒否された解除は状態も版も変えない");
    }

    [Fact]
    public async Task 段2でも保持の間の版の無い解除は428ではなく保持の409()
    {
        using var factory = Factory(hold: true, precondition: "Require");
        var client = Owner(factory);

        var response = await client.PostAsJsonAsync("/risk-controls/kill-switch/disengage", new KillSwitchRequest("再開"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeOf(response)).Should().Be(KillSwitchHeldByOperatorException.Code);
    }

    [Fact]
    public async Task 保持が無ければ古い版の解除の409はversion_conflictで区別できる()
    {
        using var factory = Factory(hold: false);
        var client = Owner(factory);
        (await client.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("急変"))).EnsureSuccessStatusCode();
        var seen = (await StateOf(client)).Version;
        (await client.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("再発"))).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/risk-controls/kill-switch/disengage", new KillSwitchRequest("再開", ExpectedVersion: seen));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeOf(response)).Should().Be(KillSwitchVersionConflictException.Code);
    }

    [Fact]
    public void 保持による起動は版を進め_保持より前の版を見た発動はversion_conflict()
    {
        var store = new InMemoryKillSwitchStore();
        var log = new InMemorySettingsChangeLog();
        var clock = new FakeClock(Now, DateOnly.FromDateTime(Now.UtcDateTime));
        var seen = new KillSwitchService(store, log, clock).GetState().Version;

        new KillSwitchService(store, log, clock, new OperatorKillSwitchHoldOptions { Enabled = true }).ApplyOperatorHold()
            .Should().Be(OperatorHoldOutcome.Engaged);
        store.GetState().Version.Should().NotBe(seen);

        var service = new KillSwitchService(store, log, clock);
        var stale = () => service.Engage("owner", "見た状態で発動", seen);
        stale.Should().Throw<KillSwitchVersionConflictException>();
        store.GetState().Actor.Should().Be(OperatorKillSwitchHoldOptions.Actor, "保持の起動の主体を古い版の操作で上書きしない");
    }
}
