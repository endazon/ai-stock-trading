using System.Net;
using System.Text;
using System.Text.Json;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Domain;
using NotificationService.Features.Notifications;
using NotificationService.Features.Notifications.OperateKillSwitch;
using NotificationService.Infrastructure.ExternalServices;
using Xunit;
using RiskProto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace NotificationService.Tests;

// FR-10, FR-14, UC-06, #1561, IADR-0620: ボットが kill switch の解除に「確認を出したときに照会した版」を添えること。
// - 版は稼働状態の照会（REST の killSwitchVersion・gRPC の kill_switch_version）から読む。照会に失敗したら null（版なしで従来どおり）。
// - 版は解除にだけ載せる（REST の本文の expectedVersion・proto の expected_version）。null なら欄ごと送らない＝本文・要求は従来と同じ。
// - 起動（止める向き）には載せない（古い版で止められなくならない）。
// - 拒否の印（REST の本文の code・gRPC の末尾の ast-error-code）で、保持・版の競合・版なしを利用者向けの文言に分ける。
public class KillSwitchVersionTransportTests
{
    private const string Version = "42";

    // ---- REST ----

    private sealed class RecordingHandler(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public List<(string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsolutePath,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("接続できない");
    }

    private static HttpKillSwitchController Rest(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://risk") }, NullLogger<HttpKillSwitchController>.Instance);

    private static string? VersionIn(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("expectedVersion", out var v) ? v.GetString() : null;
    }

    private static bool HasVersionField(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("expectedVersion", out _);
    }

    [Fact]
    public async Task REST_版つきの解除は本文の_expectedVersion_に版を載せる()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"engaged":false}""");

        (await Rest(handler).DisengageAsync("再開", null, Version)).Succeeded.Should().BeTrue();

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Path.Should().Be("/risk-controls/kill-switch/disengage");
        VersionIn(handler.Requests[0].Body).Should().Be(Version);
    }

    [Fact]
    public async Task REST_版なしの解除と起動は欄ごと送らない()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"engaged":true}""");
        var rest = Rest(handler);

        await rest.DisengageAsync("再開", null, null);
        await rest.DisengageAsync("再開", "123456");
        await rest.DisengageAsync("再開");
        await rest.EngageAsync("止める");

        handler.Requests.Should().HaveCount(4).And.OnlyContain(r => !HasVersionField(r.Body), "版なしの本文は従来と同じ");
    }

    [Theory]
    [InlineData("""{"killSwitchVersion":"42"}""", "42")]
    [InlineData("""{"killSwitchVersion":"0"}""", "0")]
    [InlineData("""{"other":1}""", null)]
    [InlineData("""{"killSwitchVersion":null}""", null)]
    [InlineData("""{"killSwitchVersion":"abc"}""", null)]
    public async Task REST_版は稼働状態の照会の_killSwitchVersion_から読み_版の形でなければ_null(string response, string? expected)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, response);

        (await Rest(handler).GetVersionAsync()).Should().Be(expected);

        handler.Requests.Should().ContainSingle().Which.Path.Should().Be("/risk-controls/status");
    }

    [Fact]
    public async Task REST_照会の失敗は_null_で例外にしない()
    {
        (await Rest(new RecordingHandler(HttpStatusCode.InternalServerError, "{}")).GetVersionAsync()).Should().BeNull();
        (await Rest(new RecordingHandler(HttpStatusCode.OK, "壊れた本文")).GetVersionAsync()).Should().BeNull();
        (await Rest(new ThrowingHandler()).GetVersionAsync()).Should().BeNull();
    }

    public static TheoryData<HttpStatusCode, string, bool, string> RestRejections() => new()
    {
        { HttpStatusCode.Conflict, "version_conflict", false, KillSwitchVersions.VersionConflictMessage },
        { HttpStatusCode.PreconditionRequired, "precondition_required", false, KillSwitchVersions.PreconditionRequiredMessage },
        { HttpStatusCode.Conflict, "operator_hold", true, HttpKillSwitchController.OperatorHoldMessage },
    };

    [Theory]
    [MemberData(nameof(RestRejections))]
    public async Task REST_拒否の印で利用者向けの文言を分ける(HttpStatusCode status, string code, bool engaged, string message)
    {
        var handler = new RecordingHandler(status, $$"""{"error":"x","code":"{{code}}"}""");

        (await Rest(handler).DisengageAsync("再開", null, Version)).Should().Be(new KillSwitchResult(false, engaged, message));
    }

    [Fact]
    public async Task REST_印の無い_428_は従来どおり番号つきの失敗()
    {
        var result = await Rest(new RecordingHandler(HttpStatusCode.PreconditionRequired, """{"error":"x"}""")).DisengageAsync("再開", null, null);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("428");
    }

    // ---- gRPC ----

    private sealed class FixedToken : IServiceAccessTokenProvider
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("bot-owner-token");
    }

    private static GrpcKillSwitchController Grpc(BotReadGrpcStubHost host) =>
        new(new RiskManagementGrpcTransport(
                GrpcClientExtensions.CreateAiStockTradingChannel(host.Address, new FixedToken()),
                TimeSpan.FromSeconds(5), 1, NullLogger<RiskManagementGrpcTransport>.Instance),
            NullLogger<GrpcKillSwitchController>.Instance);

    private static RiskProto.KillSwitchChangeRequest LastKillSwitchRequest(BotReadStubBehavior behavior) =>
        behavior.Requests.Select(r => r.Request).OfType<RiskProto.KillSwitchChangeRequest>().Last();

    [Fact]
    public async Task gRPC_版つきの解除は要求の_expected_version_を立て_版なしと起動は立てない()
    {
        var behavior = new BotReadStubBehavior();
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        var grpc = Grpc(host);

        await grpc.DisengageAsync("再開", null, Version);
        var withVersion = LastKillSwitchRequest(behavior);
        withVersion.HasExpectedVersion.Should().BeTrue();
        withVersion.ExpectedVersion.Should().Be(Version);

        await grpc.DisengageAsync("再開", "123456");
        LastKillSwitchRequest(behavior).HasExpectedVersion.Should().BeFalse();
        await grpc.EngageAsync("止める");
        LastKillSwitchRequest(behavior).HasExpectedVersion.Should().BeFalse("起動は止める向き");
    }

    [Fact]
    public async Task gRPC_版は稼働状態の照会の_kill_switch_version_から読み_欠落と失敗は_null()
    {
        var behavior = new BotReadStubBehavior
        {
            RiskStatus = BotReadStubBehavior.Returns(new RiskProto.GetRiskStatusResponse { KillSwitchVersion = Version }),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        (await Grpc(host).GetVersionAsync()).Should().Be(Version);

        behavior.RiskStatus = BotReadStubBehavior.Returns(new RiskProto.GetRiskStatusResponse());
        (await Grpc(host).GetVersionAsync()).Should().BeNull("旧版の提供側は版を返さない");

        behavior.RiskStatus = BotReadStubBehavior.Fails<RiskProto.GetRiskStatusResponse>(StatusCode.PermissionDenied);
        (await Grpc(host).GetVersionAsync()).Should().BeNull();
    }

    private static Func<int, CancellationToken, Task<RiskProto.KillSwitchChangeResponse>> Rejects(StatusCode status, string? code) =>
        (_, _) => throw new RpcException(
            new Status(status, "提供側の説明"),
            code is null ? new Metadata() : new Metadata { { NotificationGrpcCalls.ErrorCodeTrailer, code } });

    public static TheoryData<StatusCode, string?, bool, string> GrpcRejections() => new()
    {
        { StatusCode.Aborted, "version_conflict", false, KillSwitchVersions.VersionConflictMessage },
        { StatusCode.FailedPrecondition, "precondition_required", false, KillSwitchVersions.PreconditionRequiredMessage },
        { StatusCode.Aborted, "operator_hold", true, HttpKillSwitchController.OperatorHoldMessage },
        // 印を送らない旧版の提供側の解除の ABORTED は、従来どおり保持の拒否として読む。
        { StatusCode.Aborted, null, true, HttpKillSwitchController.OperatorHoldMessage },
    };

    [Theory]
    [MemberData(nameof(GrpcRejections))]
    public async Task gRPC_拒否の印で_REST_と同じ文言に分ける(StatusCode status, string? code, bool engaged, string message)
    {
        var behavior = new BotReadStubBehavior { KillSwitch = Rejects(status, code) };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);

        (await Grpc(host).DisengageAsync("再開", null, Version)).Should().Be(new KillSwitchResult(false, engaged, message));
    }

    // ---- ハンドラ ----

    private const string Guild = "guild-1";
    private const string Channel = "channel-1";
    private const string OwnerUser = "discord-owner-1";
    private const string Phrase = "STOP TRADING";

    private sealed class VersionedKillSwitch : IKillSwitchController
    {
        public List<(string Op, string? Version)> Calls { get; } = [];

        public int VersionReads { get; private set; }

        public Task<KillSwitchResult> EngageAsync(string reason, CancellationToken cancellationToken = default)
        {
            Calls.Add(("engage", null));
            return Task.FromResult(new KillSwitchResult(true, true, "起動しました"));
        }

        public Task<KillSwitchResult> DisengageAsync(string reason, CancellationToken cancellationToken = default) =>
            DisengageAsync(reason, null, null, cancellationToken);

        public Task<KillSwitchResult> DisengageAsync(
            string reason, string? secondFactorCode, string? expectedVersion, CancellationToken cancellationToken = default)
        {
            Calls.Add(("disengage", expectedVersion));
            return Task.FromResult(new KillSwitchResult(true, false, "解除しました"));
        }

        public Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
        {
            VersionReads++;
            return Task.FromResult<string?>(Version);
        }
    }

    private static DiscordBotOptions Options()
    {
        var options = new DiscordBotOptions { GuildId = Guild, ChannelId = Channel, KillSwitchConfirmationPhrase = Phrase };
        options.AllowedUserIds.Add(OwnerUser);
        options.UserMapping[OwnerUser] = "endazon";
        return options;
    }

    private static DiscordCommandContext Context(string raw, string? user = OwnerUser) => new(Guild, Channel, user, false, raw);

    private static KillSwitchCommandHandler Handler(IKillSwitchController controller) =>
        new(controller, Options(), NullLogger<KillSwitchCommandHandler>.Instance);

    [Fact]
    public async Task ハンドラは版を解除にだけ渡し_起動には渡さない()
    {
        var controller = new VersionedKillSwitch();
        var handler = Handler(controller);

        await handler.HandleAsync(Context("/killswitch off"), Phrase, null, Version);
        await handler.HandleAsync(Context("/killswitch"), Phrase, null, Version);
        await handler.HandleAsync(Context("/killswitch off"), Phrase);

        controller.Calls.Should().Equal(("disengage", Version), ("engage", null), ("disengage", null));
    }

    [Fact]
    public async Task ハンドラはフレーズで止まったら提供側を呼ばない()
    {
        var controller = new VersionedKillSwitch();

        await Handler(controller).HandleAsync(Context("/killswitch off"), "wrong phrase", null, Version);

        controller.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task 版の照会は多層認証を通った利用者にだけ行う()
    {
        var controller = new VersionedKillSwitch();
        var handler = Handler(controller);

        (await handler.GetDisengageVersionAsync(Context("/killswitch off"))).Should().Be(Version);
        (await handler.GetDisengageVersionAsync(Context("/killswitch off", user: "stranger"))).Should().BeNull("許可外へ状態を漏らさない");

        controller.VersionReads.Should().Be(1);
    }

    // 版を扱わない偽物（既定の実装）は、版を捨てて従来の口へ委ね、照会は null（試験の偽物を壊さない）。
    [Fact]
    public async Task 口の既定の実装は版を捨て_照会は_null()
    {
        IKillSwitchController legacy = new LegacyKillSwitch();

        (await legacy.DisengageAsync("再開", null, Version)).Succeeded.Should().BeTrue();
        (await legacy.GetVersionAsync()).Should().BeNull();
    }

    private sealed class LegacyKillSwitch : IKillSwitchController
    {
        public Task<KillSwitchResult> EngageAsync(string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(new KillSwitchResult(true, true, "起動しました"));

        public Task<KillSwitchResult> DisengageAsync(string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(new KillSwitchResult(true, false, "解除しました"));
    }
}
