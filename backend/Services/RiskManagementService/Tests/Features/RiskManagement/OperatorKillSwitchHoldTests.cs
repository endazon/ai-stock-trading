using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using System.Net.Http.Json;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace RiskManagementService.Tests;

// NFR, FR-10, UC-06, ADR-0009, #1530, IADR-0613: Discord を経ない停止（SSH から既存の kill switch を起動する経路）。
// 保持（operator hold）は構成で入れる。入っている間は kill switch を起動したまま保ち、解除（REST・gRPC とも同じ処理関数）を拒む。
// 外すと kill switch は起動したまま残り（DB の状態）、解除は従来どおり Discord・画面から行える。
public class OperatorKillSwitchHoldTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static (KillSwitchService Service, InMemoryKillSwitchStore Store, InMemorySettingsChangeLog Log)
        Create(OperatorKillSwitchHoldOptions? hold)
    {
        var store = new InMemoryKillSwitchStore();
        var log = new InMemorySettingsChangeLog();
        var clock = new FakeClock(Now, new DateOnly(2026, 11, 5));
        return (new KillSwitchService(store, log, clock, hold), store, log);
    }

    [Fact]
    public void 保持が無ければ起動しない_既定の振る舞いを変えない()
    {
        var (service, store, log) = Create(hold: null);

        service.ApplyOperatorHold().Should().Be(OperatorHoldOutcome.Inactive);

        store.GetState().Engaged.Should().BeFalse();
        log.GetHistory().Should().BeEmpty();
    }

    [Fact]
    public void 保持が無効の値なら起動しない()
    {
        var (service, store, _) = Create(new OperatorKillSwitchHoldOptions { Enabled = false, Reason = "x" });

        service.ApplyOperatorHold().Should().Be(OperatorHoldOutcome.Inactive);

        store.GetState().Engaged.Should().BeFalse();
    }

    [Fact]
    public void 保持が入っていれば起動し_変更履歴に保持の主体と理由で残る()
    {
        var (service, store, log) = Create(new OperatorKillSwitchHoldOptions { Enabled = true, Reason = "異常の調査" });

        service.ApplyOperatorHold().Should().Be(OperatorHoldOutcome.Engaged);

        var state = store.GetState();
        state.Engaged.Should().BeTrue();
        state.Actor.Should().Be(OperatorKillSwitchHoldOptions.Actor);
        state.Reason.Should().Be("異常の調査");
        log.GetHistory().Should().ContainSingle()
            .Which.ChangeType.Should().Be(SettingsChangeType.KillSwitchEngaged);
    }

    [Fact]
    public void 理由が空なら既定の理由で起動する_止める側を理由の欠けで失敗させない()
    {
        var (service, store, _) = Create(new OperatorKillSwitchHoldOptions { Enabled = true, Reason = "  " });

        service.ApplyOperatorHold().Should().Be(OperatorHoldOutcome.Engaged);

        store.GetState().Reason.Should().Be(OperatorKillSwitchHoldOptions.DefaultReason);
    }

    [Fact]
    public void 既に起動していれば上書きしない_先の起動の主体と理由を保つ()
    {
        var (service, store, log) = Create(new OperatorKillSwitchHoldOptions { Enabled = true, Reason = "保持" });
        service.Engage("owner", "急変");

        service.ApplyOperatorHold().Should().Be(OperatorHoldOutcome.AlreadyEngaged);

        store.GetState().Actor.Should().Be("owner");
        log.GetHistory().Should().ContainSingle("保持は 2 本目の履歴を足さない");
    }

    [Fact]
    public void 保持の間は解除を拒む()
    {
        var (service, store, _) = Create(new OperatorKillSwitchHoldOptions { Enabled = true });
        service.ApplyOperatorHold();

        var act = () => service.Disengage("owner", "再開");

        act.Should().Throw<KillSwitchHeldByOperatorException>();
        store.GetState().Engaged.Should().BeTrue();
    }

    [Fact]
    public void 保持の間も起動は受け付ける_止める側は妨げない()
    {
        var (service, store, _) = Create(new OperatorKillSwitchHoldOptions { Enabled = true });

        service.Engage("owner", "念のため");

        store.GetState().Engaged.Should().BeTrue();
    }

    [Fact]
    public void 保持を外すと起動したまま残り_従来どおり解除できる()
    {
        var store = new InMemoryKillSwitchStore();
        var log = new InMemorySettingsChangeLog();
        var clock = new FakeClock(Now, new DateOnly(2026, 11, 5));
        new KillSwitchService(store, log, clock, new OperatorKillSwitchHoldOptions { Enabled = true }).ApplyOperatorHold();

        // 保持を外した構成で作り直す（Pod の再起動に当たる）。
        var released = new KillSwitchService(store, log, clock, new OperatorKillSwitchHoldOptions { Enabled = false });

        released.ApplyOperatorHold().Should().Be(OperatorHoldOutcome.Inactive);
        store.GetState().Engaged.Should().BeTrue("保持を外しただけでは取引を再開しない");

        released.Disengage("owner", "確認済み");
        store.GetState().Engaged.Should().BeFalse();
    }
}

// 配線: 構成（RiskManagement:OperatorKillSwitchHold:*）で入れた保持が、起動時に kill switch を起動し、REST の解除を 409 で拒むこと。
public class OperatorKillSwitchHoldWiringTests
{
    private static RiskWorkerWebApplicationFactory Factory(bool enabled) => new()
    {
        HostSettings = new Dictionary<string, string?>
        {
            ["RiskManagement:OperatorKillSwitchHold:Enabled"] = enabled ? "true" : "false",
            ["RiskManagement:OperatorKillSwitchHold:Reason"] = "SSH からの停止",
        },
    };

    private static HttpClient Owner(RiskWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    [Fact]
    public async Task 保持が入っていれば起動時に_kill_switch_が起動し_解除は409()
    {
        using var factory = Factory(enabled: true);
        var client = Owner(factory);

        var state = await client.GetFromJsonAsync<KillSwitchState>("/risk-controls/kill-switch");
        state!.Engaged.Should().BeTrue();
        state.Actor.Should().Be(OperatorKillSwitchHoldOptions.Actor);
        state.Reason.Should().Be("SSH からの停止");

        var disengage = await client.PostAsJsonAsync("/risk-controls/kill-switch/disengage", new KillSwitchRequest("再開"));
        disengage.StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await client.GetFromJsonAsync<KillSwitchState>("/risk-controls/kill-switch"))!.Engaged.Should().BeTrue();
    }

    // NFR, #1530, IADR-0613: gRPC の解除（ボットの段 5 の経路・RiskControlsOwnerWriteGrpcService）も同じ処理関数を通り、保持の間は ABORTED で拒まれる。
    [Fact]
    public async Task 保持の間はgRPCの解除もABORTEDで拒まれ_状態は起動のまま()
    {
        using var factory = Factory(enabled: true);
        var grpc = new Proto.RiskControlsOwnerWrite.RiskControlsOwnerWriteClient(GrpcChannel.ForAddress(factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = new BotHeaders(factory.Server.CreateHandler()) }));

        var act = async () => await grpc.DisengageKillSwitchAsync(new Proto.KillSwitchChangeRequest { Reason = "再開" });

        var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
        ex.StatusCode.Should().Be(StatusCode.Aborted);
        (await Owner(factory).GetFromJsonAsync<KillSwitchState>("/risk-controls/kill-switch"))!.Engaged.Should().BeTrue();
    }

    // gRPC の所有者の門（GrpcOwnerOnly）は trading-owner ∧ azp＝ボットの機密クライアント（既定 ai-stock-trading-owner）を求める。
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

    [Fact]
    public async Task 保持が無効なら起動時に何もしない()
    {
        using var factory = Factory(enabled: false);
        var client = Owner(factory);

        (await client.GetFromJsonAsync<KillSwitchState>("/risk-controls/kill-switch"))!.Engaged.Should().BeFalse();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<OperatorKillSwitchHoldOptions>().Enabled.Should().BeFalse();
    }
}
