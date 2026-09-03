namespace MarketMonitorService.Features.MarketMonitor.GetWatchlist;

// FR-02, IADR-0095: 取得は read（OwnerOrService）に置き、定時サイクル（#11 TradeDecision）が s2s 同期照会できるようにする。
internal static class GetWatchlistEndpoint
{
    public static void MapGetWatchlist(this IEndpointRouteBuilder read) =>
        read.MapGet("/watchlist", (MonitorWatchlistService svc) => Results.Ok(svc.GetWatchlist()));
}
