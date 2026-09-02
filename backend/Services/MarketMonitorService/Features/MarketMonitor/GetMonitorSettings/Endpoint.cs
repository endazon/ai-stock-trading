namespace MarketMonitorService.Features.MarketMonitor.GetMonitorSettings;

// ---- 監視設定（変動閾値・クールダウン・監視銘柄の一括置換。後方互換で従来どおり） ----
internal static class GetMonitorSettingsEndpoint
{
    public static void MapGetMonitorSettings(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/settings", (IMonitoredSymbolStore store) => Results.Ok(store.GetSettings()));
}
