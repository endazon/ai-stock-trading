using Grpc.Core;
using Microsoft.Extensions.Logging;
using NotificationService.Features.Notifications;
using ReportProto = AiStockTrading.Shared.Grpc.Report.V1;

namespace NotificationService.Infrastructure.ExternalServices;

// NFR, FR-07, FR-13, FR-14, MSP:ADR-0029, ADR-0047 決定 1・2, IADR-0284 決定 5（段 5）, IADR-0449 決定 4, IADR-0450 決定 4, #753:
// 方針の改訂の 2 つ目の実装。入れ替え案の照会は `ReportOwnerRead/GetWatchlistProposal`（段 5 の前半）、方針の改訂と適用の内訳の記録は
// `ReportOwnerWrite/RevisePolicy`・`RecordWatchlistApplyResult`（段 5 の後半）。`Reports:Grpc` を宣言したときだけ選ばれる。
// 🔴 方針の改訂は**冪等でない**（LLM を呼び新しい版を作り、1 日の回数を消費する）。**再試行しない**。deadline は REST と同じ 120 秒
// （`Reports:GrpcPolicyRevisionTimeoutSeconds`）。届いたか分からない失敗（時間切れ・不達）は「不明」（案が保存されたかもしれない）、
// 提供側が明確に拒否した失敗は「案なし」（REST の非 2xx と同じ＝提供側は 200 以外で何も保存しない契約）。REST へ落とさない。
// 🔴 REST の 404 ＝ NOT_FOUND（案ではない）、409 ＝ FAILED_PRECONDITION（その版で確定されていない＝適用しない。PR #1027 の監査 H1）。
// 🔴 案の解釈は REST と同じ 1 つ（`HttpPolicyRevisionController.InterpretProposal`）。
public sealed class GrpcPolicyRevisionController(
    ReportsGrpcTransport transport,
    ILogger<GrpcPolicyRevisionController> logger)
    : IPolicyRevisionController
{
    public async Task<PolicyRevisionCommandOutcome> ReviseAsync(
        string? periodKey,
        string instruction,
        string onBehalfOf,
        IReadOnlyList<WatchlistSnapshotItemView>? currentWatchlist,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);
        ArgumentException.ThrowIfNullOrWhiteSpace(onBehalfOf);

        var request = new ReportProto.PolicyRevisionProposalRequest { Instruction = instruction, OnBehalfOf = onBehalfOf };
        if (periodKey is not null)
            request.PeriodKey = periodKey;
        // 🔴 照会できなかった（null）は入れ物ごと付けない（空の一覧と区別する＝REST の null と同じ）。
        if (currentWatchlist is not null)
        {
            request.CurrentWatchlist = new ReportProto.WatchlistSnapshotRows();
            request.CurrentWatchlist.Items.AddRange(currentWatchlist.Select(w => new ReportProto.WatchlistSnapshotRow { Symbol = w.Symbol, Market = w.Market }));
        }

        var outcome = await transport.PolicyRevisionCalls.CallOnceAsync(
            "方針の改訂", options => transport.OwnerWrite.RevisePolicyAsync(request, options), cancellationToken).ConfigureAwait(false);

        if (outcome.Response is not { } response)
        {
            if (NotificationGrpcCalls.IsOutcomeUnknown(outcome.Status))
                return new PolicyRevisionCommandOutcome(false, true, HttpPolicyRevisionController.RevisionUnknownMessage);

            logger.LogWarning("方針の改訂が受理されませんでした（gRPC {Status}）。", outcome.Status);
            var hint = outcome.Status is StatusCode.Unauthenticated or StatusCode.PermissionDenied ? NotificationGrpcCalls.OwnerHint : string.Empty;
            return HttpPolicyRevisionController.Rejected($"gRPC {outcome.Status}", NotificationGrpcCalls.ProviderReason(outcome), hint);
        }

        return HttpPolicyRevisionController.InterpretRevision(NotificationGrpcWire.ToRevisionView(response), logger);
    }

    public async Task<WatchlistProposalLookup> GetWatchlistProposalAsync(
        string periodKey, int version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);

        var outcome = await transport.Calls.CallAsync(
            "入れ替え案の照会",
            options => transport.OwnerRead.GetWatchlistProposalAsync(
                new ReportProto.GetWatchlistProposalRequest { PeriodKey = periodKey, Version = version }, options),
            cancellationToken).ConfigureAwait(false);

        if (outcome.Response is not { } response)
        {
            return outcome.Status switch
            {
                StatusCode.NotFound => new WatchlistProposalLookup(true, false, null, HttpPolicyRevisionController.NotProposalMessage),
                StatusCode.FailedPrecondition =>
                    new WatchlistProposalLookup(true, false, null, HttpPolicyRevisionController.NotConfirmedAtVersionMessage),
                // REST の例外（応答が届かない）と同じ文言。
                StatusCode.DeadlineExceeded or StatusCode.Unavailable =>
                    new WatchlistProposalLookup(false, false, null, "入れ替え案を照会できませんでした（応答が届きませんでした）"),
                _ => new WatchlistProposalLookup(false, false, null, $"入れ替え案を照会できませんでした（gRPC {outcome.Status}）"),
            };
        }

        var view = NotificationGrpcWire.ToProposalView(response);
        if (view is null)
            logger.LogWarning("入れ替え案の応答を解釈できませんでした（gRPC）。");
        return HttpPolicyRevisionController.InterpretProposal(view, periodKey);
    }

    // 🔴 記録は 1 回だけ（2 回目は ABORTED）。**再試行しない**。失敗はすべて false（REST と同じ＝記録できなかった）。
    public async Task<bool> RecordWatchlistApplyAsync(
        Guid attemptId,
        string outcome,
        IReadOnlyList<WatchlistApplyItemView> items,
        string message,
        string onBehalfOf,
        CancellationToken cancellationToken = default)
    {
        var request = new ReportProto.WatchlistApplyRecordRequest
        {
            AttemptId = attemptId.ToString(),
            Outcome = outcome,
            Message = message,
            OnBehalfOf = onBehalfOf,
        };
        request.Items.AddRange(items.Select(i =>
        {
            var row = new ReportProto.WatchlistApplyRecordItem { Action = i.Action, Symbol = i.Symbol, Applied = i.Applied };
            if (i.SkipReason is not null)
                row.SkipReason = i.SkipReason;
            return row;
        }));

        var result = await transport.PolicyRevisionCalls.CallOnceAsync(
            "入れ替え案の適用の内訳の記録", options => transport.OwnerWrite.RecordWatchlistApplyResultAsync(request, options),
            cancellationToken).ConfigureAwait(false);
        if (result.Response is null)
            logger.LogWarning("入れ替え案の適用の内訳を記録できませんでした（gRPC {Status}）。", result.Status);
        return result.Response is not null;
    }
}
