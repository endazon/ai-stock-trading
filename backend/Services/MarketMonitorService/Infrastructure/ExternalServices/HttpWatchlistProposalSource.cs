using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MarketMonitorService.Features.MarketMonitor.ApplyWatchlistProposal;

namespace MarketMonitorService.Infrastructure.ExternalServices;

/// <summary>
/// NFR-06, IADR-0634 決定 2, IADR-0420: 報告書サービスの <c>GET /reports/policy-revisions/watchlist-proposal/binding</c> をサービスの身元で呼ぶ。
/// 400 / 404 / 409 は台帳の判定、それ以外の非 2xx・例外・読めない応答は Unavailable。
/// </summary>
public sealed class HttpWatchlistProposalSource(HttpClient http, ILogger<HttpWatchlistProposalSource> logger) : IWatchlistProposalSource
{
    public const string Route = "/reports/policy-revisions/watchlist-proposal/binding";

    public const string ReserveRoute = "/reports/policy-revisions/watchlist-proposal/binding/reserve";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public async Task<WatchlistProposalLookupResult> FindAsync(string periodKey, int version, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(
                $"{Route}?periodKey={Uri.EscapeDataString(periodKey)}&version={version}", cancellationToken).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case HttpStatusCode.BadRequest:
                    return new(WatchlistProposalLookupStatus.Invalid, null);
                case HttpStatusCode.NotFound:
                    return new(WatchlistProposalLookupStatus.NotProposal, null);
                case HttpStatusCode.Conflict:
                    return new(WatchlistProposalLookupStatus.NotConfirmed, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("{Marker} 台帳が照会に応じませんでした（{Status}）。確かめられないものとして扱います。",
                    WatchlistProposalBindingGate.LogMarker, (int)response.StatusCode);
                return new(WatchlistProposalLookupStatus.Unavailable, null);
            }

            var view = await response.Content.ReadFromJsonAsync<BindingView>(Web, cancellationToken).ConfigureAwait(false);
            if (view?.Proposal is not { Changes: { } changes } proposal)
                return new(WatchlistProposalLookupStatus.Unavailable, null);

            return new(WatchlistProposalLookupStatus.Found, new BoundWatchlistProposal(
                changes, proposal.Snapshot, proposal.ApplyRecorded, view.SecondFactorVerified, view.ApplyReserved));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "{Marker} 台帳へ届きませんでした。確かめられないものとして扱います。", WatchlistProposalBindingGate.LogMarker);
            return new(WatchlistProposalLookupStatus.Unavailable, null);
        }
    }

    // NFR-06, IADR-0634 決定 8: 適用の予約。200＝予約した／409＝予約・記録済み（2 度目）・確定されていない／それ以外は届かない。
    // 本文は読まない（状態だけで決める）。
    public async Task<WatchlistProposalReserveStatus> ReserveAsync(string periodKey, int version, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.PostAsync(
                $"{ReserveRoute}?periodKey={Uri.EscapeDataString(periodKey)}&version={version}", content: null, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return WatchlistProposalReserveStatus.Reserved;
            if (response.StatusCode == HttpStatusCode.Conflict)
                return WatchlistProposalReserveStatus.AlreadyReserved;
            logger.LogWarning("{Marker} 台帳が予約に応じませんでした（{Status}）。確かめられないものとして扱います。",
                WatchlistProposalBindingGate.LogMarker, (int)response.StatusCode);
            return WatchlistProposalReserveStatus.Unavailable;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "{Marker} 台帳へ予約を届けられませんでした。確かめられないものとして扱います。", WatchlistProposalBindingGate.LogMarker);
            return WatchlistProposalReserveStatus.Unavailable;
        }
    }

    private sealed record BindingView(ProposalView? Proposal, bool SecondFactorVerified, bool ApplyReserved);

    private sealed record ProposalView(
        IReadOnlyList<BoundWatchlistChange>? Changes, IReadOnlyList<BoundWatchlistSnapshotEntry>? Snapshot, bool ApplyRecorded);
}

