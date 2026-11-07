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
                changes, proposal.Snapshot, proposal.ApplyRecorded, view.SecondFactorEnforced));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "{Marker} 台帳へ届きませんでした。確かめられないものとして扱います。", WatchlistProposalBindingGate.LogMarker);
            return new(WatchlistProposalLookupStatus.Unavailable, null);
        }
    }

    private sealed record BindingView(ProposalView? Proposal, bool SecondFactorEnforced);

    private sealed record ProposalView(
        IReadOnlyList<BoundWatchlistChange>? Changes, IReadOnlyList<BoundWatchlistSnapshotEntry>? Snapshot, bool ApplyRecorded);
}

