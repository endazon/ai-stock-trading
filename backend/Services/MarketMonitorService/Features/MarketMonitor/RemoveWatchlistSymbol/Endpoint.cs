using Microsoft.AspNetCore.Mvc;

namespace MarketMonitorService.Features.MarketMonitor.RemoveWatchlistSymbol;

// ---- 監視銘柄（watchlist）の削除（FR-13, UC-06, IADR-0088/0095）----
// DELETE に body を持たせて理由を POST と対称に運ぶ（内部メッシュ限定 API・IADR-0088）。
// DELETE は body 推論を許さないため [FromBody] を明示する。
internal static class RemoveWatchlistSymbolEndpoint
{
    public static void MapRemoveWatchlistSymbol(this IEndpointRouteBuilder owner) =>
        owner.MapDelete("/watchlist", ([FromBody] WatchlistChangeRequest req, MonitorWatchlistService svc, HttpContext http) =>
            Results.Ok(svc.Remove(req.Symbol, MonitorSettingsEndpoints.MarketOf(req), MonitorSettingsEndpoints.ActorOf(http), req.Reason)));
}
