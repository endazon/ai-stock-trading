using System.Text.Json;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using ReportService.Common.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports.ConfirmReport;
using ReportService.Features.Reports.RegenerateReport;
using ReportService.Features.Reports.RequestReportChanges;
using ReportService.Features.Reports.RevisePolicy;
using ReportService.Features.Reports.WatchlistProposal;
using Wolverine;
using Proto = AiStockTrading.Shared.Grpc.Report.V1;

namespace ReportService.Features.Reports;

// NFR, NFR-06, FR-07, FR-09, FR-13, FR-14, MSP:ADR-0029, MSP:ADR-0075, ADR-0047 決定 1〜3, IADR-0284 決定 5（段 5）, IADR-0450, #753:
// 報告書の**所有者限定の書き込み**の gRPC 面（呼び出し元は Discord ボットだけ）。各 rpc は REST の端点と**同じ処理関数**を呼ぶ:
//   - `POST /reports/{periodKey}/confirm` → ConfirmReportEndpoint.HandleAsync（確定者の解決・版番号付きの冪等・監査・KB・台帳の確定時刻）
//   - `POST /reports/{periodKey}/request-changes` → RequestReportChangesEndpoint.Handle
//   - `POST /reports/policy-revisions` → RevisePolicyEndpoint.HandleAsync
//   - `POST /reports/policy-revisions/{attemptId}/watchlist-apply-result` → WatchlistProposalEndpoints.RecordApplyResult
//   - `POST /reports/{periodKey}/regenerate` → RegenerateReportEndpoint.HandleAsync（FR-06, 計画 ADR-0052, #1156, IADR-0491 決定 1）
// 処理関数の結果（REST の状態と本文）を gRPC の状態へ写すのは ReportWriteGrpcReplies（REST の群のフィルタの例外の写しも同じものを使う）。
//
// 🔴 門は **`GrpcOwnerOnly`**。REST の OwnerOnly と同じく s2s には開かない（生成AI・自動処理は確定できない＝ADR-0003）。
// 🔴 **並走中の正は REST である**（MSP:ADR-0029 の 2026-08-04 追記）。REST 面の応答は変えていない。
[Authorize(Policy = AiStockTradingAuthPolicies.GrpcOwnerOnly)]
public sealed class ReportOwnerWriteGrpcService(
    ReportAppService reports,
    ReportPolicyRevisionService revisions,
    IMessageBus bus,
    IKnowledgeBaseWriter kb,
    IPolicyRevisionLedger ledger,
    IReportStore store,
    IClock clock,
    DelegatedActorOptions delegated,
    ILoggerFactory loggerFactory,
    ReportRegenerationService regenerations,
    // FR-06, FR-08, #1300, IADR-0526 決定 3: 確定で承認待ちの写し（ドラフト）を消す（REST の確定と同じ処理を共有する）。
    IReportDraftKnowledgeCopy? draftCopy = null)
    : Proto.ReportOwnerWrite.ReportOwnerWriteBase
{
    // NFR-06, IADR-0503, #1206: 例外の写しが固定文言に置き換えたときの元の例外の出し先。
    private ILogger Logger => loggerFactory.CreateLogger<ReportOwnerWriteGrpcService>();

    public override async Task<Proto.ReportConfirmationResponse> ConfirmReport(
        Proto.ReportConfirmationRequest request, ServerCallContext context)
    {
        var reply = await ReportWriteGrpcReplies.RunAsync(Logger, () => ConfirmReportEndpoint.HandleAsync(
            PeriodKeyOf(request.PeriodKey),
            new ConfirmReportRequest(request.ExpectedVersion, request.HasOnBehalfOf ? request.OnBehalfOf : null),
            reports, bus, kb, loggerFactory, delegated, ledger, context.GetHttpContext(), draftCopy));
        var confirmed = reply.ValueOrThrow<ConfirmReportResponse>();
        return new Proto.ReportConfirmationResponse { Transitioned = confirmed.Transitioned, Version = confirmed.Version };
    }

    public override async Task<Proto.ReportChangesResponse> RequestReportChanges(
        Proto.ReportChangesRequest request, ServerCallContext context)
    {
        var reply = await ReportWriteGrpcReplies.RunAsync(Logger, () => RequestReportChangesEndpoint.Handle(
            PeriodKeyOf(request.PeriodKey), new ReviewCommandRequest(request.ExpectedVersion), reports, context.GetHttpContext()));
        return new Proto.ReportChangesResponse { Version = reply.ValueOrThrow<ReportReview>().Version };
    }

    public override async Task<Proto.PolicyRevisionProposalResponse> RevisePolicy(
        Proto.PolicyRevisionProposalRequest request, ServerCallContext context)
    {
        var reply = await ReportWriteGrpcReplies.RunAsync(Logger, () => RevisePolicyEndpoint.HandleAsync(
            new RevisePolicyRequest(
                request.Instruction,
                request.HasPeriodKey ? request.PeriodKey : null,
                request.HasOnBehalfOf ? request.OnBehalfOf : null,
                // 🔴 入れ物の欠落＝ REST の null（照会できなかった）。空の入れ物＝空の一覧。
                request.CurrentWatchlist is { } current
                    ? [.. current.Items.Select(e => new WatchlistSnapshotEntry(e.HasSymbol ? e.Symbol : null, e.HasMarket ? e.Market : null))]
                    : null),
            revisions, delegated, loggerFactory, context.GetHttpContext()));
        return ReportWriteWireMapping.ToProto(reply.ValueOrThrow<PolicyRevisionResponse>());
    }

    public override async Task<Proto.WatchlistApplyRecordResponse> RecordWatchlistApplyResult(
        Proto.WatchlistApplyRecordRequest request, ServerCallContext context)
    {
        // REST の経路引数は `{attemptId:guid}` の制約で Guid でなければ 404（経路に当たらない）。gRPC では入力の誤りとして返す。
        if (!Guid.TryParse(request.AttemptId, out var attemptId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "試行 ID（attemptId）が Guid ではありません。"));

        var reply = await ReportWriteGrpcReplies.RunAsync(Logger, () => WatchlistProposalEndpoints.RecordApplyResult(
            attemptId,
            new WatchlistApplyResultRequest(
                request.Outcome,
                [.. request.Items.Select(i => new WatchlistApplyItemRecord(
                    i.HasAction ? i.Action : null, i.HasSymbol ? i.Symbol : null, i.Applied, i.HasSkipReason ? i.SkipReason : null))],
                request.HasMessage ? request.Message : null,
                request.HasOnBehalfOf ? request.OnBehalfOf : null),
            ledger, store, clock, delegated, context.GetHttpContext()));
        return new Proto.WatchlistApplyRecordResponse { AttemptId = reply.ValueOrThrow<WatchlistApplyRecordedResponse>().AttemptId.ToString() };
    }

    // FR-06, FR-14, 計画 ADR-0052, #1156, IADR-0491 決定 1: 作り直し（REST と同じ処理関数。冪等でない＝呼び出し側は再試行しない）。
    public override async Task<Proto.ReportRegenerationReply> RegenerateReport(
        Proto.ReportRegenerationRequest request, ServerCallContext context)
    {
        var reply = await ReportWriteGrpcReplies.RunAsync(Logger, () => RegenerateReportEndpoint.HandleAsync(
            PeriodKeyOf(request.PeriodKey),
            new RegenerateReportRequest(request.HasOnBehalfOf ? request.OnBehalfOf : null),
            regenerations, delegated, loggerFactory, context.GetHttpContext()));
        return ReportWriteWireMapping.ToProto(reply.ValueOrThrow<ReportRegenerationResponse>());
    }

    // REST の経路引数は空になり得ない。gRPC では空を「対象が無い」ではなく入力の誤りとして返す（読み取りの面と同じ）。
    private static string PeriodKeyOf(string periodKey) =>
        string.IsNullOrWhiteSpace(periodKey)
            ? throw new RpcException(new Status(StatusCode.InvalidArgument, "会話キー（periodKey）が必要です。"))
            : periodKey;
}

// NFR, IADR-0450 決定 2: REST の処理関数の結果（`IResult`）→ gRPC の状態。REST の群のフィルタと同じ例外の写し（ReportEndpoints.MapException）を通す。
// 🔴 サービスを跨いで共通化しない（リスク管理・市場監視はそれぞれの写しを持つ＝群のフィルタの例外の種類が違う。IADR-0264 決定 1 と同じ向き）。
internal static class ReportWriteGrpcReplies
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal static Task<Reply> RunAsync(ILogger logger, Func<IResult> handler) => RunAsync(logger, () => Task.FromResult(handler()));

    internal static async Task<Reply> RunAsync(ILogger logger, Func<Task<IResult>> handler)
    {
        IResult result;
        try
        {
            result = await handler().ConfigureAwait(false);
        }
        catch (Exception e) when (ReportEndpoints.MapException(e, logger) is { } mapped)
        {
            result = mapped;
        }

        return new Reply(
            (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK,
            (result as IValueHttpResult)?.Value);
    }

    // REST の状態 → gRPC の状態（REST と同じ分類を保つ）。502（AI の案を作れなかった）は INTERNAL ＝提供側が明確に失敗した（何も保存していない）。
    internal static StatusCode ToGrpcStatus(int httpStatus) => httpStatus switch
    {
        StatusCodes.Status400BadRequest => StatusCode.InvalidArgument,
        StatusCodes.Status401Unauthorized => StatusCode.Unauthenticated,
        StatusCodes.Status403Forbidden => StatusCode.PermissionDenied,
        StatusCodes.Status404NotFound => StatusCode.NotFound,
        StatusCodes.Status409Conflict => StatusCode.Aborted,
        StatusCodes.Status422UnprocessableEntity => StatusCode.FailedPrecondition,
        StatusCodes.Status429TooManyRequests => StatusCode.ResourceExhausted,
        _ => StatusCode.Internal,
    };

    internal readonly record struct Reply(int Status, object? Value)
    {
        /// <summary>200 で期待した型の本文なら本文。それ以外は REST と同じ分類の gRPC の失敗（REST の <c>error</c> を詳細に載せる）。</summary>
        internal T ValueOrThrow<T>() where T : class =>
            Status == StatusCodes.Status200OK && Value is T value ? value : throw ToRpcException();

        internal RpcException ToRpcException() =>
            new(new Status(Status == StatusCodes.Status200OK ? StatusCode.Internal : ToGrpcStatus(Status), ErrorOf(Value) ?? string.Empty));
    }

    internal static string? ErrorOf(object? value)
    {
        if (value is null)
            return null;

        var json = JsonSerializer.SerializeToElement(value, value.GetType(), Web);
        return json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
    }
}

// NFR, IADR-0450 決定 3: 書き込みの応答型 → 線上表現（提供側の写し）。C# の null は設定しない。
public static class ReportWriteWireMapping
{
    // FR-06, 計画 ADR-0052, IADR-0491 決定 1: 作り直しの応答の線上表現。
    public static Proto.ReportRegenerationReply ToProto(ReportRegenerationResponse regeneration)
    {
        ArgumentNullException.ThrowIfNull(regeneration);

        var reply = new Proto.ReportRegenerationReply
        {
            PeriodKey = regeneration.PeriodKey,
            PreviousVersion = regeneration.PreviousVersion,
            Version = regeneration.Version,
            Presented = regeneration.Presented,
            Message = regeneration.Message,
        };
        reply.UnsuppliedInputs.AddRange(regeneration.UnsuppliedInputs);
        reply.NotRestorableInputs.AddRange(regeneration.NotRestorableInputs);
        return reply;
    }

    public static Proto.PolicyRevisionProposalResponse ToProto(PolicyRevisionResponse revision)
    {
        ArgumentNullException.ThrowIfNull(revision);

        var response = new Proto.PolicyRevisionProposalResponse
        {
            Version = revision.Version,
            Created = revision.Created,
            Presented = revision.Presented,
        };
        if (revision.PeriodKey is not null) response.PeriodKey = revision.PeriodKey;
        if (revision.Message is not null) response.Message = revision.Message;
        if (revision.PolicySummary is not null) response.PolicySummary = revision.PolicySummary;
        if (revision.Rationale is not null) response.Rationale = revision.Rationale;
        response.WatchlistChanges.AddRange(revision.WatchlistChanges.Select(c =>
        {
            var row = new Proto.WatchlistChangeRow();
            if (c is null) return row;
            if (c.Action is not null) row.Action = c.Action;
            if (c.Symbol is not null) row.Symbol = c.Symbol;
            if (c.Reason is not null) row.Reason = c.Reason;
            return row;
        }));
        return response;
    }
}
