using Grpc.Core;
using NotificationService.Features.Notifications;
using MonitorProto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;

namespace NotificationService.Infrastructure.ExternalServices;

// NFR, FR-13, FR-14, MSP:ADR-0029, ADR-0047 決定 1・2, IADR-0284 決定 5（段 5）, IADR-0449 決定 4, #753:
// 監視銘柄の 2 つ目の実装。**監視銘柄の照会（読み取り）だけ**を gRPC（`WatchlistRead/GetWatchlist`。段 4 の面をそのまま使う）で行い、
// 入れ替え案の適用（書き込み）は REST の実装（HttpMarketMonitorWatchlistController）へ委ねる（段 5 の後半で移す）。
// `MarketMonitor:Grpc` を宣言したときだけ選ばれる。
// 🔴 一覧の解釈は REST と同じ 1 つ（`InterpretWatchlist`＝ 1 行でも欠ければ一覧ごと解釈不能）。照会の失敗は「空」ではなく失敗。
public sealed class GrpcMarketMonitorWatchlistController(
    MarketMonitorGrpcTransport transport,
    IMarketMonitorWatchlistController rest)
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

        return HttpMarketMonitorWatchlistController.InterpretWatchlist([.. response.Items.Select(NotificationGrpcWire.ToSymbolView)]);
    }

    public Task<WatchlistApplyOutcome> ApplyProposalAsync(
        IReadOnlyList<WatchlistSnapshotItemView> expected,
        IReadOnlyList<WatchlistChangeSuggestionView> changes,
        string proposalRef,
        string onBehalfOf,
        CancellationToken cancellationToken = default) =>
        rest.ApplyProposalAsync(expected, changes, proposalRef, onBehalfOf, cancellationToken);
}
