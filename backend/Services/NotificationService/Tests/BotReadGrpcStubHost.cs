using System.Collections.Concurrent;
using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitorProto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;
using ReportProto = AiStockTrading.Shared.Grpc.Report.V1;
using RiskProto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace NotificationService.Tests;

// NFR, FR-14, IADR-0449, IADR-0450, #753（段 5）: Discord ボットが呼ぶ 7 つの gRPC 面（読み取り: リスク管理の `RiskControlsRead`・
// `RiskControlsOwnerRead`、報告書の `ReportOwnerRead`、市場監視の `WatchlistRead`。書き込み: `RiskControlsOwnerWrite`・`ReportOwnerWrite`・
// `WatchlistOwnerWrite`）を**実 Kestrel の h2c 専用ポート**（127.0.0.1 のみ）で立てる偽の提供側。受け取った要求も記録する（on_behalf_of 等の観測点）。
// 🔴 受け取った `authorization` メタデータを記録する（ボットの owner トークンが載ることの観測点）。
// あわせて、owner トークンの取得先（client_credentials）を **HTTP/1.1 の別ポート**（127.0.0.1 のみ）で立てられる
// （平文の 1 ポートで HTTP/1.1 と h2c を同時に受けられないため）。
internal sealed class BotReadGrpcStubHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private BotReadGrpcStubHost(WebApplication app, BotReadStubBehavior behavior, string address, string tokenEndpoint)
    {
        _app = app;
        Behavior = behavior;
        Address = address;
        TokenEndpoint = tokenEndpoint;
    }

    internal BotReadStubBehavior Behavior { get; }

    /// <summary>gRPC（h2c）の宛先。</summary>
    internal string Address { get; }

    /// <summary>owner トークンの取得先（HTTP/1.1。常に <see cref="BotReadStubBehavior.IssuedToken"/> を返す）。</summary>
    internal string TokenEndpoint { get; }

    internal static async Task<BotReadGrpcStubHost> StartAsync(BotReadStubBehavior behavior)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2);
            k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http1);
        });
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(behavior);

        var app = builder.Build();
        app.MapGrpcService<RiskReadStub>();
        app.MapGrpcService<RiskOwnerReadStub>();
        app.MapGrpcService<ReportOwnerReadStub>();
        app.MapGrpcService<WatchlistReadStub>();
        app.MapGrpcService<RiskOwnerWriteStub>();
        app.MapGrpcService<ReportOwnerWriteStub>();
        app.MapGrpcService<WatchlistOwnerWriteStub>();
        app.MapPost("/token", (HttpContext http) =>
        {
            behavior.TokenRequests.Enqueue(http.Request.Protocol);
            return Results.Json(new Dictionary<string, object> { ["access_token"] = behavior.IssuedToken, ["expires_in"] = 300 });
        });
        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ToList();
        // 登録順（h2c → HTTP/1.1）で並ぶ。
        return new BotReadGrpcStubHost(app, behavior, addresses[0], addresses[1].TrimEnd('/') + "/token");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed class BotReadStubBehavior
{
    private readonly ConcurrentDictionary<string, int> _calls = new();

    internal string IssuedToken { get; init; } = "bot-owner-token";

    internal ConcurrentQueue<string> TokenRequests { get; } = new();

    /// <summary>rpc の名前ごとに受け取った <c>authorization</c>（無ければ空文字）。</summary>
    internal ConcurrentQueue<(string Rpc, string Authorization)> Received { get; } = new();

    internal int Calls(string rpc) => _calls.GetValueOrDefault(rpc);

    internal Func<int, CancellationToken, Task<RiskProto.GetRiskStatusResponse>> RiskStatus { get; set; } =
        (_, _) => Task.FromResult(new RiskProto.GetRiskStatusResponse());

    internal Func<int, CancellationToken, Task<RiskProto.GetStageGateResponse>> StageGate { get; set; } =
        (_, _) => Task.FromResult(new RiskProto.GetStageGateResponse());

    internal Func<int, CancellationToken, Task<ReportProto.GetReportReviewResponse>> Review { get; set; } =
        (_, _) => Task.FromResult(new ReportProto.GetReportReviewResponse());

    internal Func<int, CancellationToken, Task<ReportProto.ListReportPeriodKeysResponse>> PeriodKeys { get; set; } =
        (_, _) => Task.FromResult(new ReportProto.ListReportPeriodKeysResponse());

    internal Func<int, CancellationToken, Task<ReportProto.GetWatchlistProposalResponse>> Proposal { get; set; } =
        (_, _) => Task.FromResult(new ReportProto.GetWatchlistProposalResponse());

    internal Func<int, CancellationToken, Task<MonitorProto.GetWatchlistResponse>> Watchlist { get; set; } =
        (_, _) => Task.FromResult(new MonitorProto.GetWatchlistResponse());

    // ---- 書き込み（段 5 の後半） ----

    internal Func<int, CancellationToken, Task<RiskProto.KillSwitchChangeResponse>> KillSwitch { get; set; } =
        (_, _) => Task.FromResult(new RiskProto.KillSwitchChangeResponse());

    internal Func<int, CancellationToken, Task<RiskProto.TradingPauseChangeResponse>> Pause { get; set; } =
        (_, _) => Task.FromResult(new RiskProto.TradingPauseChangeResponse());

    internal Func<int, CancellationToken, Task<RiskProto.GoodFaithViolationClearanceResponse>> GoodFaith { get; set; } =
        (_, _) => Task.FromResult(new RiskProto.GoodFaithViolationClearanceResponse());

    internal Func<int, CancellationToken, Task<RiskProto.StageTransitionApprovalResponse>> Transition { get; set; } =
        (_, _) => Task.FromResult(new RiskProto.StageTransitionApprovalResponse());

    internal Func<int, CancellationToken, Task<RiskProto.WithdrawalEvaluationResponse>> Withdrawal { get; set; } =
        (_, _) => Task.FromResult(new RiskProto.WithdrawalEvaluationResponse());

    internal Func<int, CancellationToken, Task<RiskProto.DriftAdoptionCommandResponse>> Adoption { get; set; } =
        (_, _) => Task.FromResult(new RiskProto.DriftAdoptionCommandResponse());

    internal Func<int, CancellationToken, Task<ReportProto.ReportConfirmationResponse>> Confirm { get; set; } =
        (_, _) => Task.FromResult(new ReportProto.ReportConfirmationResponse());

    internal Func<int, CancellationToken, Task<ReportProto.ReportChangesResponse>> Changes { get; set; } =
        (_, _) => Task.FromResult(new ReportProto.ReportChangesResponse());

    internal Func<int, CancellationToken, Task<ReportProto.PolicyRevisionProposalResponse>> Revision { get; set; } =
        (_, _) => Task.FromResult(new ReportProto.PolicyRevisionProposalResponse());

    internal Func<int, CancellationToken, Task<ReportProto.WatchlistApplyRecordResponse>> ApplyRecord { get; set; } =
        (_, _) => Task.FromResult(new ReportProto.WatchlistApplyRecordResponse());

    // FR-06, 計画 ADR-0052, #1156, IADR-0491 決定 1: 報告書の作り直し。
    internal Func<int, CancellationToken, Task<ReportProto.ReportRegenerationReply>> Regenerate { get; set; } =
        (_, _) => Task.FromResult(new ReportProto.ReportRegenerationReply());

    internal Func<int, CancellationToken, Task<MonitorProto.WatchlistProposalApplicationResponse>> Apply { get; set; } =
        (_, _) => Task.FromResult(new MonitorProto.WatchlistProposalApplicationResponse());

    /// <summary>rpc の名前ごとに受け取った要求（書き込みの本文の観測点）。</summary>
    internal ConcurrentQueue<(string Rpc, Google.Protobuf.IMessage Request)> Requests { get; } = new();

    internal Task<T> Handle<T>(string rpc, ServerCallContext context, Func<int, CancellationToken, Task<T>> behavior)
    {
        Received.Enqueue((rpc, context.RequestHeaders.GetValue("authorization") ?? string.Empty));
        return behavior(_calls.AddOrUpdate(rpc, 1, (_, n) => n + 1), context.CancellationToken);
    }

    internal Task<T> Handle<T>(string rpc, Google.Protobuf.IMessage request, ServerCallContext context, Func<int, CancellationToken, Task<T>> behavior)
    {
        Requests.Enqueue((rpc, request));
        return Handle(rpc, context, behavior);
    }

    internal static Func<int, CancellationToken, Task<T>> Fails<T>(StatusCode status, string detail) =>
        (_, _) => throw new RpcException(new Status(status, detail));

    internal static Func<int, CancellationToken, Task<T>> Returns<T>(T response) => (_, _) => Task.FromResult(response);

    internal static Func<int, CancellationToken, Task<T>> Fails<T>(StatusCode status) =>
        (call, _) => throw new RpcException(new Status(status, $"stub failure #{call}"));

    internal static Func<int, CancellationToken, Task<T>> Hangs<T>() =>
        async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        };
}

internal sealed class RiskReadStub(BotReadStubBehavior b) : RiskProto.RiskControlsRead.RiskControlsReadBase
{
    public override Task<RiskProto.GetStageGateResponse> GetStageGate(RiskProto.GetStageGateRequest request, ServerCallContext context) =>
        b.Handle(nameof(GetStageGate), context, b.StageGate);
}

internal sealed class RiskOwnerReadStub(BotReadStubBehavior b) : RiskProto.RiskControlsOwnerRead.RiskControlsOwnerReadBase
{
    public override Task<RiskProto.GetRiskStatusResponse> GetRiskStatus(RiskProto.GetRiskStatusRequest request, ServerCallContext context) =>
        b.Handle(nameof(GetRiskStatus), context, b.RiskStatus);
}

internal sealed class ReportOwnerReadStub(BotReadStubBehavior b) : ReportProto.ReportOwnerRead.ReportOwnerReadBase
{
    public override Task<ReportProto.GetReportReviewResponse> GetReportReview(
        ReportProto.GetReportReviewRequest request, ServerCallContext context) =>
        b.Handle(nameof(GetReportReview), context, b.Review);

    public override Task<ReportProto.ListReportPeriodKeysResponse> ListReportPeriodKeys(
        ReportProto.ListReportPeriodKeysRequest request, ServerCallContext context) =>
        b.Handle(nameof(ListReportPeriodKeys), context, b.PeriodKeys);

    public override Task<ReportProto.GetWatchlistProposalResponse> GetWatchlistProposal(
        ReportProto.GetWatchlistProposalRequest request, ServerCallContext context) =>
        b.Handle(nameof(GetWatchlistProposal), context, b.Proposal);
}

internal sealed class WatchlistReadStub(BotReadStubBehavior b) : MonitorProto.WatchlistRead.WatchlistReadBase
{
    public override Task<MonitorProto.GetWatchlistResponse> GetWatchlist(MonitorProto.GetWatchlistRequest request, ServerCallContext context) =>
        b.Handle(nameof(GetWatchlist), context, b.Watchlist);
}

internal sealed class RiskOwnerWriteStub(BotReadStubBehavior b) : RiskProto.RiskControlsOwnerWrite.RiskControlsOwnerWriteBase
{
    public override Task<RiskProto.KillSwitchChangeResponse> EngageKillSwitch(RiskProto.KillSwitchChangeRequest request, ServerCallContext context) =>
        b.Handle(nameof(EngageKillSwitch), request, context, b.KillSwitch);

    public override Task<RiskProto.KillSwitchChangeResponse> DisengageKillSwitch(RiskProto.KillSwitchChangeRequest request, ServerCallContext context) =>
        b.Handle(nameof(DisengageKillSwitch), request, context, b.KillSwitch);

    public override Task<RiskProto.TradingPauseChangeResponse> PauseTrading(RiskProto.TradingPauseChangeRequest request, ServerCallContext context) =>
        b.Handle(nameof(PauseTrading), request, context, b.Pause);

    public override Task<RiskProto.TradingPauseChangeResponse> ResumeTrading(RiskProto.TradingPauseChangeRequest request, ServerCallContext context) =>
        b.Handle(nameof(ResumeTrading), request, context, b.Pause);

    public override Task<RiskProto.GoodFaithViolationClearanceResponse> ClearGoodFaithViolations(
        RiskProto.GoodFaithViolationClearanceRequest request, ServerCallContext context) =>
        b.Handle(nameof(ClearGoodFaithViolations), request, context, b.GoodFaith);

    public override Task<RiskProto.StageTransitionApprovalResponse> RequestStageTransition(
        RiskProto.StageTransitionApprovalRequest request, ServerCallContext context) =>
        b.Handle(nameof(RequestStageTransition), request, context, b.Transition);

    public override Task<RiskProto.WithdrawalEvaluationResponse> EvaluateWithdrawal(
        RiskProto.WithdrawalEvaluationRequest request, ServerCallContext context) =>
        b.Handle(nameof(EvaluateWithdrawal), request, context, b.Withdrawal);

    public override Task<RiskProto.DriftAdoptionCommandResponse> AdoptPositionDrift(
        RiskProto.DriftAdoptionCommandRequest request, ServerCallContext context) =>
        b.Handle(nameof(AdoptPositionDrift), request, context, b.Adoption);
}

internal sealed class ReportOwnerWriteStub(BotReadStubBehavior b) : ReportProto.ReportOwnerWrite.ReportOwnerWriteBase
{
    public override Task<ReportProto.ReportConfirmationResponse> ConfirmReport(ReportProto.ReportConfirmationRequest request, ServerCallContext context) =>
        b.Handle(nameof(ConfirmReport), request, context, b.Confirm);

    public override Task<ReportProto.ReportChangesResponse> RequestReportChanges(ReportProto.ReportChangesRequest request, ServerCallContext context) =>
        b.Handle(nameof(RequestReportChanges), request, context, b.Changes);

    public override Task<ReportProto.PolicyRevisionProposalResponse> RevisePolicy(
        ReportProto.PolicyRevisionProposalRequest request, ServerCallContext context) =>
        b.Handle(nameof(RevisePolicy), request, context, b.Revision);

    public override Task<ReportProto.WatchlistApplyRecordResponse> RecordWatchlistApplyResult(
        ReportProto.WatchlistApplyRecordRequest request, ServerCallContext context) =>
        b.Handle(nameof(RecordWatchlistApplyResult), request, context, b.ApplyRecord);

    public override Task<ReportProto.ReportRegenerationReply> RegenerateReport(
        ReportProto.ReportRegenerationRequest request, ServerCallContext context) =>
        b.Handle(nameof(RegenerateReport), request, context, b.Regenerate);
}

internal sealed class WatchlistOwnerWriteStub(BotReadStubBehavior b) : MonitorProto.WatchlistOwnerWrite.WatchlistOwnerWriteBase
{
    public override Task<MonitorProto.WatchlistProposalApplicationResponse> ApplyWatchlistProposal(
        MonitorProto.WatchlistProposalApplicationRequest request, ServerCallContext context) =>
        b.Handle(nameof(ApplyWatchlistProposal), request, context, b.Apply);
}
