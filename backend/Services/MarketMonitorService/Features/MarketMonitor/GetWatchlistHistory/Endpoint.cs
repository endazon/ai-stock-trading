namespace MarketMonitorService.Features.MarketMonitor.GetWatchlistHistory;

internal static class GetWatchlistHistoryEndpoint
{
    public static void MapGetWatchlistHistory(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/watchlist/history", (MonitorWatchlistService svc) => Results.Ok(svc.GetHistory()));
}
