namespace MarketMonitorService.Features.MarketMonitor.AddWatchlistSymbol;

// ---- 監視銘柄（watchlist）の追加（FR-13, UC-06, IADR-0088/0095）----
// 追加は理由必須（reason 空欄は 400）。actor は認証済みトークン名（preferred_username）から取る。
// 重複追加・空 symbol・未定義 market は 400、設定行の Version 楽観排他競合は 409（親の例外フィルタで写像）。
// 変更（追加/削除）は owner（OwnerOnly）据え置き＝変更は利用者のみ（FR-13）維持。
// FR-13, SC-02, ADR-0043（計画）決定 2 (b)・4, #1030, IADR-0437: 足した後に Finnhub の 1 巡回が巡回間隔に収まらないなら 400
// （Discord の入れ替え案の適用と同じ検査。Finnhub を使わない構成では検査しない）。
internal static class AddWatchlistSymbolEndpoint
{
    public static void MapAddWatchlistSymbol(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/watchlist", async (WatchlistChangeRequest req, MonitorWatchlistService svc, WatchlistCycleFitGuard guard,
            HttpContext http) =>
        {
            var market = MonitorSettingsEndpoints.MarketOf(req);
            var fit = await guard.ResolveAsync(http.RequestAborted);
            return Results.Ok(svc.Add(req.Symbol, market, MonitorSettingsEndpoints.ActorOf(http), req.Reason, fit?.Fit));
        });
}
