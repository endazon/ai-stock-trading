using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Infrastructure.Composable.SecondFactor;
using RiskManagementService.Features.RiskManagement;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace RiskManagementService.Tests;

// NFR-06, FR-14, #1542, IADR-0615: リスク管理の危険な操作（kill switch の解除・再開・段階の昇格・GFV 停止の解除・乖離の取り込み）は
// 第 2 要素（TOTP）を提供側で確かめる。止める向き（kill switch の起動・一時停止）には課さない。段 1（Audit）は記録だけ。
// 🔴 決定 9: 門は入力の検証・冪等の判定の後、状態を変える直前にだけ呼ぶ。受理できない要求・既に済んだ再送はコードを消費しない。
public class OwnerSecondFactorWiringTests
{
    // RFC 6238 付録 B の SHA1 の公開の試験値の種（ASCII の "1234567890" を 2 回）を、実行時に base32 へ組み立てる（本物の秘密ではない。
    // 文字列の定数で書くと秘密の走査に掛かる）。
    private static readonly string Secret = Totp.EncodeBase32(System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("1234567890", 2))));

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

    [Fact]
    public async Task Enforceでも既に解除済み_再開済みの再送はコードなしで従来どおり200()
    {
        using var factory = Factory("Enforce");
        var client = Owner(factory);

        (await client.PostAsJsonAsync("/risk-controls/kill-switch/disengage", new KillSwitchRequest("再送"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/risk-controls/resume", new PauseRequest("再送"))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 受理できない要求はコードを消費しない_同じコードを次の正しい操作で使える()
    {
        using var factory = Factory("Enforce");
        var client = Owner(factory);
        var code = CurrentCode();

        // 解除する GFV 違反が無い（422）・乖離の観測が無い（受理不能）・昇格の基準が未充足（422）・理由が空（400）。いずれも門を通らない。
        (await client.PostAsJsonAsync("/risk-controls/good-faith-violations/clear", new { Reason = "是正済み", SecondFactorCode = code }))
            .StatusCode.Should().NotBe(HttpStatusCode.Forbidden).And.NotBe(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/risk-controls/position-drift/adopt", new { Symbol = "AAPL", Market = 1, Reason = "台帳を合わせる", SecondFactorCode = code }))
            .StatusCode.Should().NotBe(HttpStatusCode.Forbidden).And.NotBe(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/risk-controls/stage-gate/transition", new { TargetStage = 1, SecondFactorCode = code }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        await client.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("止める"));
        (await client.PostAsJsonAsync("/risk-controls/kill-switch/disengage", new KillSwitchRequest("", code))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await client.PostAsJsonAsync("/risk-controls/kill-switch/disengage", new KillSwitchRequest("再開", code)))
            .StatusCode.Should().Be(HttpStatusCode.OK, "受理できない要求でコードが消費されていない");
    }

    [Fact]
    public async Task 受理できない要求は失敗の数にも入らない_5回のあとも正しいコードで通る()
    {
        using var factory = Factory("Enforce");
        var client = Owner(factory);
        for (var i = 0; i < 6; i++)
            await client.PostAsJsonAsync("/risk-controls/good-faith-violations/clear", new { Reason = "是正済み", SecondFactorCode = "000000" });
        await client.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("止める"));

        (await client.PostAsJsonAsync("/risk-controls/kill-switch/disengage", new KillSwitchRequest("再開", CurrentCode())))
            .StatusCode.Should().Be(HttpStatusCode.OK, "ロックされていない");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Off")]
    [InlineData("Audit")]
    public async Task 既定と段1はコードが無くても解除する(string? mode)
    {
        using var factory = Factory(mode);
        var client = Owner(factory);
        await client.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("止める"));

        (await client.PostAsJsonAsync("/risk-controls/kill-switch/disengage", new KillSwitchRequest("再開"))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // NFR-06, #1542, IADR-0615 決定 4: gRPC の面（ボットの段 5 の経路）でも Enforce の拒否は PERMISSION_DENIED。
    [Fact]
    public async Task EnforceのgRPCの解除はコードが無ければPERMISSION_DENIED()
    {
        using var factory = Factory("Enforce");
        await Owner(factory).PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("止める"));
        var grpc = new Proto.RiskControlsOwnerWrite.RiskControlsOwnerWriteClient(GrpcChannel.ForAddress(factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = new BotHeaders(factory.Server.CreateHandler()) }));

        var act = async () => await grpc.DisengageKillSwitchAsync(new Proto.KillSwitchChangeRequest { Reason = "再開" });

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        (await grpc.DisengageKillSwitchAsync(new Proto.KillSwitchChangeRequest { Reason = "再開", SecondFactorCode = CurrentCode() }))
            .Engaged.Should().BeFalse();
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
}
