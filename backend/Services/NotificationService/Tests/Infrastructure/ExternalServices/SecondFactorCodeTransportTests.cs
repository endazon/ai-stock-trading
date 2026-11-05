using System.Net;
using System.Text;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Features.Notifications;
using NotificationService.Infrastructure.ExternalServices;
using Xunit;
using ReportProto = AiStockTrading.Shared.Grpc.Report.V1;
using RiskProto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace NotificationService.Tests;

// NFR-06, FR-14, UC-06, #1552, IADR-0615: ボットが確認コードを**取引を再開・緩める 6 本の書き込み**（方針の確定は報告書の確定と同じ口）で
// 提供側へ運ぶことを、REST の本文と gRPC の要求の両方で固定する。
//
// - 入れたコードは、提供側が読む欄（REST の `secondFactorCode`・proto の `second_factor_code`）にそのまま載る。
// - 入れなければ（null）欄ごと送らない＝本文・要求は従来と同じ（段 1 の Audit の「コードなし」に当たる）。
// - 止める向き（kill switch の起動・一時停止）の口には欄を載せる経路が無い。
public class SecondFactorCodeTransportTests
{
    // 6 桁の形の試験用の値（鍵から作ったものではない。提供側の門の試験は別の場所で RFC の試験値を使う）。
    private const string Code = "246810";

    // ---- REST ----

    private sealed class RecordingHandler(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private static HttpClient Client(RecordingHandler handler) => new(handler) { BaseAddress = new Uri("http://provider") };

    private static string? CodeIn(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("secondFactorCode", out var code) ? code.GetString() : null;
    }

    private static bool HasCodeField(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("secondFactorCode", out _);
    }

    // 各口を「コードつき」「コードなし」で 1 回ずつ呼び、送った本文を返す。
    public static TheoryData<string> RestPorts() =>
        ["kill-switch-disengage", "resume", "gfv-clear", "stage-promote", "drift-adopt", "report-confirm"];

    private static async Task<IReadOnlyList<string>> CallRestAsync(string port, string? code)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        var http = Client(handler);
        switch (port)
        {
            case "kill-switch-disengage":
                await new HttpKillSwitchController(http, NullLogger<HttpKillSwitchController>.Instance).DisengageAsync("r", code);
                break;
            case "resume":
                await new HttpPauseController(http, NullLogger<HttpPauseController>.Instance).ResumeAsync("r", code);
                break;
            case "gfv-clear":
                await new HttpGoodFaithViolationController(http, NullLogger<HttpGoodFaithViolationController>.Instance).ClearAsync("r", code);
                break;
            case "stage-promote":
                await new HttpStageGateController(http, NullLogger<HttpStageGateController>.Instance).RequestTransitionAsync(2, "owner-a", code);
                break;
            case "drift-adopt":
                await new HttpPositionDriftAdoptionController(http, NullLogger<HttpPositionDriftAdoptionController>.Instance)
                    .AdoptAsync("AAPL", Market.UnitedStates, "r", "owner-a", code);
                break;
            case "report-confirm":
                await new HttpReportReviewController(http, NullLogger<HttpReportReviewController>.Instance)
                    .ConfirmAsync("daily-2026-09-01", 3, "owner-a", code);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(port), port, null);
        }

        return handler.Bodies;
    }

    [Theory]
    [MemberData(nameof(RestPorts))]
    public async Task REST_入れたコードは本文の_secondFactorCode_にそのまま載る(string port)
    {
        var bodies = await CallRestAsync(port, Code);

        bodies.Should().ContainSingle();
        CodeIn(bodies[0]).Should().Be(Code, $"{port} は提供側が読む欄でコードを運ぶ");
    }

    [Theory]
    [MemberData(nameof(RestPorts))]
    public async Task REST_コードなしは欄ごと送らず本文は従来どおり(string port)
    {
        var bodies = await CallRestAsync(port, null);

        bodies.Should().ContainSingle();
        HasCodeField(bodies[0]).Should().BeFalse($"{port} は null の欄を送らない（旧版の提供側にも同じ本文が届く）");
    }

    // 既存の口（コードを取らない形）は、新しい口へコードなしで委ねる＝本文は従来どおり。
    [Fact]
    public async Task REST_従来の口と止める向きの口は欄を送らない()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        var http = Client(handler);

        await new HttpKillSwitchController(http, NullLogger<HttpKillSwitchController>.Instance).EngageAsync("r");
        await new HttpKillSwitchController(http, NullLogger<HttpKillSwitchController>.Instance).DisengageAsync("r");
        await new HttpPauseController(http, NullLogger<HttpPauseController>.Instance).PauseAsync("r");
        await new HttpPauseController(http, NullLogger<HttpPauseController>.Instance).ResumeAsync("r");
        await new HttpStageGateController(http, NullLogger<HttpStageGateController>.Instance).RequestTransitionAsync(1, "owner-a");
        await new HttpReportReviewController(http, NullLogger<HttpReportReviewController>.Instance).ConfirmAsync("daily-2026-09-01", 3, "owner-a");

        handler.Bodies.Should().HaveCount(6).And.OnlyContain(b => !HasCodeField(b));
    }

    // 段階遷移の本文の既存の欄は、record に替えても名前が変わらない（提供側は targetStage・onBehalfOf を読む）。
    [Fact]
    public async Task REST_段階遷移の本文の既存の欄名は変わらない()
    {
        var bodies = await CallRestAsync("stage-promote", Code);

        using var doc = JsonDocument.Parse(bodies[0]);
        doc.RootElement.GetProperty("targetStage").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("onBehalfOf").GetString().Should().Be("owner-a");
    }

    // ---- gRPC ----

    private sealed class FixedToken : IServiceAccessTokenProvider
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("bot-owner-token");
    }

    private static Grpc.Net.Client.GrpcChannel Channel(BotReadGrpcStubHost host) =>
        GrpcClientExtensions.CreateAiStockTradingChannel(host.Address, new FixedToken());

    private static RiskManagementGrpcTransport Risk(BotReadGrpcStubHost host) =>
        new(Channel(host), TimeSpan.FromSeconds(5), 1, NullLogger<RiskManagementGrpcTransport>.Instance);

    private static ReportsGrpcTransport Reports(BotReadGrpcStubHost host) =>
        new(Channel(host), TimeSpan.FromSeconds(5), 1, NullLogger<ReportsGrpcTransport>.Instance, TimeSpan.FromSeconds(5),
            confirmTimeout: TimeSpan.FromSeconds(5));

    private static async Task CallGrpcAsync(BotReadGrpcStubHost host, string? code)
    {
        await new GrpcKillSwitchController(Risk(host), NullLogger<GrpcKillSwitchController>.Instance).DisengageAsync("r", code);
        await new GrpcPauseController(Risk(host), NullLogger<GrpcPauseController>.Instance).ResumeAsync("r", code);
        await new GrpcGoodFaithViolationController(Risk(host), NullLogger<GrpcGoodFaithViolationController>.Instance).ClearAsync("r", code);
        await new GrpcStageGateController(Risk(host), NullLogger<GrpcStageGateController>.Instance).RequestTransitionAsync(2, "owner-a", code);
        await new GrpcPositionDriftAdoptionController(Risk(host), NullLogger<GrpcPositionDriftAdoptionController>.Instance)
            .AdoptAsync("AAPL", Market.UnitedStates, "r", "owner-a", code);
        await new GrpcReportReviewController(Reports(host), NullLogger<GrpcReportReviewController>.Instance)
            .ConfirmAsync("daily-2026-09-01", 3, "owner-a", code);
    }

    private static readonly string[] LooseningRpcs =
    [
        "DisengageKillSwitch", "ResumeTrading", "ClearGoodFaithViolations", "RequestStageTransition", "AdoptPositionDrift", "ConfirmReport",
    ];

    private static (bool Has, string Value) CodeOf(Google.Protobuf.IMessage request) => request switch
    {
        RiskProto.KillSwitchChangeRequest r => (r.HasSecondFactorCode, r.SecondFactorCode),
        RiskProto.TradingPauseChangeRequest r => (r.HasSecondFactorCode, r.SecondFactorCode),
        RiskProto.GoodFaithViolationClearanceRequest r => (r.HasSecondFactorCode, r.SecondFactorCode),
        RiskProto.StageTransitionApprovalRequest r => (r.HasSecondFactorCode, r.SecondFactorCode),
        RiskProto.DriftAdoptionCommandRequest r => (r.HasSecondFactorCode, r.SecondFactorCode),
        ReportProto.ReportConfirmationRequest r => (r.HasSecondFactorCode, r.SecondFactorCode),
        _ => throw new InvalidOperationException(request.GetType().Name),
    };

    [Fact]
    public async Task gRPC_入れたコードは要求の_second_factor_code_にそのまま載る()
    {
        var behavior = new BotReadStubBehavior();
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);

        await CallGrpcAsync(host, Code);

        behavior.Requests.Select(r => r.Rpc).Should().BeEquivalentTo(LooseningRpcs);
        behavior.Requests.Select(r => CodeOf(r.Request)).Should().OnlyContain(c => c.Has && c.Value == Code);
    }

    [Fact]
    public async Task gRPC_コードなしは欄を立てない()
    {
        var behavior = new BotReadStubBehavior();
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);

        await CallGrpcAsync(host, null);

        behavior.Requests.Select(r => r.Rpc).Should().BeEquivalentTo(LooseningRpcs);
        behavior.Requests.Should().OnlyContain(r => !CodeOf(r.Request).Has, "optional の欄は入れたときだけ立つ");
    }

    [Fact]
    public async Task gRPC_止める向きの口は欄を立てない()
    {
        var behavior = new BotReadStubBehavior();
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);

        await new GrpcKillSwitchController(Risk(host), NullLogger<GrpcKillSwitchController>.Instance).EngageAsync("r");
        await new GrpcPauseController(Risk(host), NullLogger<GrpcPauseController>.Instance).PauseAsync("r");

        behavior.Requests.Select(r => r.Rpc).Should().BeEquivalentTo(["EngageKillSwitch", "PauseTrading"]);
        behavior.Requests.Should().OnlyContain(r => !CodeOf(r.Request).Has);
    }
}
