namespace MarketMonitorService.Features.MarketMonitor.GetMonitorSettingsHistory;

// 監視設定の変更履歴（監視銘柄・収集パラメータを 1 本の台帳で返す）。`/watchlist/history` は同じ台帳の
// 別名であり、監視銘柄の文脈から辿るために残す（既存の消費者を壊さない）。
internal static class GetMonitorSettingsHistoryEndpoint
{
    public static void MapGetMonitorSettingsHistory(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/settings/history", (MonitorSettingsService svc) => Results.Ok(svc.GetHistory()));
}
