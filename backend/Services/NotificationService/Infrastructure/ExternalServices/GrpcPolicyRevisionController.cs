using Grpc.Core;
using Microsoft.Extensions.Logging;
using NotificationService.Features.Notifications;
using ReportProto = AiStockTrading.Shared.Grpc.Report.V1;

namespace NotificationService.Infrastructure.ExternalServices;

// NFR, FR-07, FR-13, FR-14, MSP:ADR-0029, ADR-0047 決定 1・2, IADR-0284 決定 5（段 5）, IADR-0449 決定 4, #753:
// 方針の改訂の 2 つ目の実装。**入れ替え案の照会（読み取り）だけ**を gRPC（`ReportOwnerRead/GetWatchlistProposal`）で行い、方針の改訂と適用の
// 内訳の記録（書き込み）は REST の実装（HttpPolicyRevisionController）へ委ねる（段 5 の後半で移す）。`Reports:Grpc` を宣言したときだけ選ばれる。
// 🔴 REST の 404 ＝ NOT_FOUND（案ではない）、409 ＝ FAILED_PRECONDITION（その版で確定されていない＝適用しない。PR #1027 の監査 H1）。
// 🔴 案の解釈は REST と同じ 1 つ（`HttpPolicyRevisionController.InterpretProposal`）。
public sealed class GrpcPolicyRevisionController(
    ReportsGrpcTransport transport,
    IPolicyRevisionController rest,
    ILogger<GrpcPolicyRevisionController> logger)
    : IPolicyRevisionController
{
    public Task<PolicyRevisionCommandOutcome> ReviseAsync(
        string? periodKey,
        string instruction,
        string onBehalfOf,
        IReadOnlyList<WatchlistSnapshotItemView>? currentWatchlist,
        CancellationToken cancellationToken = default) =>
        rest.ReviseAsync(periodKey, instruction, onBehalfOf, currentWatchlist, cancellationToken);

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

    public Task<bool> RecordWatchlistApplyAsync(
        Guid attemptId,
        string outcome,
        IReadOnlyList<WatchlistApplyItemView> items,
        string message,
        string onBehalfOf,
        CancellationToken cancellationToken = default) =>
        rest.RecordWatchlistApplyAsync(attemptId, outcome, items, message, onBehalfOf, cancellationToken);
}
