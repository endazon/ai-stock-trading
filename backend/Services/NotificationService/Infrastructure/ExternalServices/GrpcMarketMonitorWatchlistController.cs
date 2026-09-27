using Grpc.Core;
using Microsoft.Extensions.Logging;
using NotificationService.Features.Notifications;
using MonitorProto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;

namespace NotificationService.Infrastructure.ExternalServices;

// NFR, FR-13, FR-14, MSP:ADR-0029, ADR-0047 決定 1・2, IADR-0284 決定 5（段 5）, IADR-0449 決定 4, IADR-0450 決定 4, #753:
// 監視銘柄の 2 つ目の実装。監視銘柄の照会は `WatchlistRead/GetWatchlist`（段 4 の面）、入れ替え案の適用は `WatchlistOwnerWrite/ApplyWatchlistProposal`
// （段 5 の後半）。`MarketMonitor:Grpc` を宣言したときだけ選ばれる。変更者は本文の on_behalf_of で運ぶ（ADR-0047 決定 1）。
// 🔴 一覧の解釈は REST と同じ 1 つ（`InterpretWatchlist`＝ 1 行でも欠ければ一覧ごと解釈不能）。照会の失敗は「空」ではなく失敗。
// 🔴 適用は**再試行しない**（2 回目は楽観排他で ABORTED になり、適用済みなのに「適用していない」に見える）。届いたか分からない失敗は
// 「不明」、提供側が明確に拒否した失敗は「1 件も適用していない」（REST と同じ 3 値）。REST へ落とさない。
public sealed class GrpcMarketMonitorWatchlistController(
    MarketMonitorGrpcTransport transport,
    ILogger<GrpcMarketMonitorWatchlistController> logger)
    : IMarketMonitorWatchlistController
{
    public async Task<WatchlistSnapshotResult> GetWatchlistAsync(CancellationToken cancellationToken = default)
    {
        var outcome = await transport.Calls.CallAsync(
            "監視銘柄の照会",
            options => transport.Read.GetWatchlistAsync(new MonitorProto.GetWatchlistRequest(), options),
            cancellationToken).ConfigureAwait(false);

        if (outcome.Response is not { } response)
        {
            return outcome.Status is StatusCode.DeadlineExceeded or StatusCode.Unavailable
                ? new WatchlistSnapshotResult(false, [], "監視銘柄を照会できませんでした（応答が届きませんでした）")
                : new WatchlistSnapshotResult(false, [], $"監視銘柄を照会できませんでした（gRPC {outcome.Status}）");
        }

        var result = HttpMarketMonitorWatchlistController.InterpretWatchlist([.. response.Items.Select(NotificationGrpcWire.ToSymbolView)]);
        // NFR, IADR-0450, #753（PR #1069 の claude-review の軽微指摘）: 他の Grpc* と同じく、解釈に失敗したら Warning を残す（可観測性を揃える）。
        if (!result.Succeeded)
            logger.LogWarning("監視銘柄の応答を解釈できませんでした（gRPC・{Count} 行）。", response.Items.Count);
        return result;
    }

    public async Task<WatchlistApplyOutcome> ApplyProposalAsync(
        IReadOnlyList<WatchlistSnapshotItemView> expected,
        IReadOnlyList<WatchlistChangeSuggestionView> changes,
        string proposalRef,
        string onBehalfOf,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentException.ThrowIfNullOrWhiteSpace(onBehalfOf);

        var request = new MonitorProto.WatchlistProposalApplicationRequest
        {
            ExpectedWatchlist = new MonitorProto.WatchlistItems(),
            Changes = new MonitorProto.WatchlistChangeInstructions(),
            ProposalRef = proposalRef,
            OnBehalfOf = onBehalfOf,
        };
        request.ExpectedWatchlist.Items.AddRange(expected.Select(e =>
            new MonitorProto.WatchlistItem { Symbol = e.Symbol, Market = NotificationGrpcWire.ToMonitorMarket(e.Market) }));
        request.Changes.Items.AddRange(changes.Select(c =>
            new MonitorProto.WatchlistChangeInstruction { Action = c.Action, Symbol = c.Symbol, Reason = c.Reason }));

        var outcome = await transport.Calls.CallOnceAsync(
            "入れ替え案の適用", options => transport.OwnerWrite.ApplyWatchlistProposalAsync(request, options), cancellationToken).ConfigureAwait(false);

        if (outcome.Response is not { } response)
        {
            if (NotificationGrpcCalls.IsOutcomeUnknown(outcome.Status))
                return new WatchlistApplyOutcome(WatchlistApplyStatus.Indeterminate, [], null, HttpMarketMonitorWatchlistController.ApplyUnknownMessage);

            logger.LogWarning("入れ替え案の適用が受理されませんでした（gRPC {Status}）。", outcome.Status);
            return outcome.Status == StatusCode.Aborted
                ? HttpMarketMonitorWatchlistController.Stale(NotificationGrpcCalls.ProviderReason(outcome))
                : HttpMarketMonitorWatchlistController.Rejected(NotificationGrpcCalls.ProviderReason(outcome), $"gRPC {outcome.Status}");
        }

        return HttpMarketMonitorWatchlistController.InterpretApplied(NotificationGrpcWire.ToApplyView(response));
    }
}
