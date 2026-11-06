using System.Net.Http.Json;
using System.Text.Json;
using RiskManagementService.Features.RiskManagement;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace RiskManagementService.Tests;

// FR-10, FR-14, UC-06, #1561, IADR-0620: Discord ボットが kill switch の解除に「見た状態」の版を添えるための提供側の口。
// - 稼働状態の照会（REST /risk-controls/status・gRPC GetRiskStatus）が kill switch の版を返す（`GET /kill-switch` の版と同じ値）。
// - gRPC の解除が expected_version を受け、REST と同じく比べる（違えば ABORTED で何も変えない）。
// - gRPC の拒否は、REST の本文の code（operator_hold・version_conflict・precondition_required）を末尾の metadata で運ぶ。
//   版なしの拒否（REST の 428）は FAILED_PRECONDITION。
public class KillSwitchVersionForBotTests
{
    private static RiskWorkerWebApplicationFactory Factory(string? precondition = null, bool hold = false)
    {
        var settings = new Dictionary<string, string?>();
        if (precondition is not null)
            settings["RiskControls:KillSwitch:Precondition"] = precondition;
        if (hold)
        {
            settings["RiskManagement:OperatorKillSwitchHold:Enabled"] = "true";
            settings["RiskManagement:OperatorKillSwitchHold:Reason"] = "SSH からの停止";
        }

        return new RiskWorkerWebApplicationFactory { HostSettings = settings };
    }

    private static HttpClient Owner(RiskWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    private sealed class BotHeaders(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, "trading-owner");
            request.Headers.TryAddWithoutValidation(TestAuthHandler.NameHeader, "risk-bot");
            request.Headers.TryAddWithoutValidation(TestAuthHandler.AzpHeader, "ai-stock-trading-owner");
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static GrpcChannel Channel(RiskWorkerWebApplicationFactory factory) =>
        GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = new BotHeaders(factory.Server.CreateHandler()) });

    private static async Task<string> KillSwitchVersion(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/risk-controls/kill-switch"));
        return doc.RootElement.GetProperty("version").GetString()!;
    }

    private static async Task<string?> StatusVersion(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/risk-controls/status"));
        return doc.RootElement.TryGetProperty("killSwitchVersion", out var v) ? v.GetString() : null;
    }

    [Fact]
    public async Task 稼働状態の照会は_REST_と_gRPC_の両方で_kill_switch_の版を返し_書くたびに進む()
    {
        using var factory = Factory();
        var client = Owner(factory);
        var read = new Proto.RiskControlsOwnerRead.RiskControlsOwnerReadClient(Channel(factory));

        var initial = await StatusVersion(client);
        initial.Should().Be(await KillSwitchVersion(client));
        (await read.GetRiskStatusAsync(new Proto.GetRiskStatusRequest())).KillSwitchVersion.Should().Be(initial);

        await client.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("止める"));

        var after = await StatusVersion(client);
        after.Should().NotBe(initial).And.Be(await KillSwitchVersion(client));
        (await read.GetRiskStatusAsync(new Proto.GetRiskStatusRequest())).KillSwitchVersion.Should().Be(after);
    }

    [Fact]
    public async Task gRPC_の解除は見た版が今の版なら解除し_古ければ_ABORTED_と_version_conflict_で何も変えない()
    {
        using var factory = Factory();
        var client = Owner(factory);
        var write = new Proto.RiskControlsOwnerWrite.RiskControlsOwnerWriteClient(Channel(factory));
        await client.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("止める"));
        var seen = await KillSwitchVersion(client);
        await client.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("もう一度止める（版が進む）"));

        var stale = async () => await write.DisengageKillSwitchAsync(new Proto.KillSwitchChangeRequest { Reason = "再開", ExpectedVersion = seen });
        var ex = (await stale.Should().ThrowAsync<RpcException>()).Which;
        ex.StatusCode.Should().Be(StatusCode.Aborted);
        ex.Trailers.GetValue("ast-error-code").Should().Be("version_conflict");
        (await client.GetFromJsonAsync<KillSwitchState>("/risk-controls/kill-switch"))!.Engaged.Should().BeTrue("拒んだら何も変えない");

        var current = await KillSwitchVersion(client);
        (await write.DisengageKillSwitchAsync(new Proto.KillSwitchChangeRequest { Reason = "再開", ExpectedVersion = current }))
            .Engaged.Should().BeFalse();
    }

    [Fact]
    public async Task 段2の_gRPC_の解除は版が無ければ_FAILED_PRECONDITION_と_precondition_required()
    {
        using var factory = Factory(precondition: "Require");
        var client = Owner(factory);
        var write = new Proto.RiskControlsOwnerWrite.RiskControlsOwnerWriteClient(Channel(factory));
        await client.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("止める"));

        var act = async () => await write.DisengageKillSwitchAsync(new Proto.KillSwitchChangeRequest { Reason = "再開" });

        var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
        ex.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        ex.Trailers.GetValue("ast-error-code").Should().Be("precondition_required");
        (await write.DisengageKillSwitchAsync(new Proto.KillSwitchChangeRequest { Reason = "再開", ExpectedVersion = await KillSwitchVersion(client) }))
            .Engaged.Should().BeFalse("版を添えれば段 2 でも解除する");
    }

    [Fact]
    public async Task 保持の間の_gRPC_の解除は_ABORTED_と_operator_hold_で版の競合と見分けられる()
    {
        using var factory = Factory(hold: true);
        var client = Owner(factory);
        var write = new Proto.RiskControlsOwnerWrite.RiskControlsOwnerWriteClient(Channel(factory));

        var act = async () => await write.DisengageKillSwitchAsync(
            new Proto.KillSwitchChangeRequest { Reason = "再開", ExpectedVersion = await KillSwitchVersion(client) });

        var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
        ex.StatusCode.Should().Be(StatusCode.Aborted);
        ex.Trailers.GetValue("ast-error-code").Should().Be("operator_hold");
    }
}
