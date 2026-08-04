using AiStockTrading.MarketMonitor.Application.Ports;
using AiStockTrading.MarketMonitor.Application.Services;
using AiStockTrading.MarketMonitor.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.MarketMonitor.Api.Foundation.Endpoints;

// FR-03, FR-13, UC-06: 監視設定（監視銘柄・変動閾値・クールダウン）の照会・変更。
// 変更（PUT /settings・POST/DELETE /watchlist）と履歴は利用者のみ（OwnerOnly）＝生成AI・自動処理はこのロールを持たず変更できない。
// FR-02, IADR-0095: 監視銘柄の取得（GET /watchlist）のみサービス（trading-service）にも開放（OwnerOrService）＝定時サイクルが s2s 照会する。
// IADR-0088: 認可は read/owner サブグループに付与し、親グループには付けない（親は例外→HTTP 写像のみ）。Risk `/risk-controls` と同型。
internal static class MonitorSettingsEndpoints
{
    public static IEndpointRouteBuilder MapMonitorSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/monitor")
            .WithTags("MarketMonitor")
            // 検証失敗は 400、設定の楽観排他競合（IADR-0012）は 409 に写像（既定の 500 を回避）。読み書き共通。
            .AddEndpointFilter(async (ctx, next) =>
            {
                try
                {
                    return await next(ctx);
                }
                catch (ArgumentException e)
                {
                    return Results.BadRequest(new { error = e.Message });
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Results.Conflict(new { error = "設定が他の更新と競合しました。最新を取得して再試行してください。" });
                }
            });

        // ---- 読み取り系: 利用者またはサービス（IADR-0051/0095・OwnerOrService） ----
        // 未認証は 401、trading-owner/trading-service いずれも持たなければ 403。認可は read サブグループに付与し親には付けない。
        var read = g.MapGroup("").RequireAuthorization(AiStockTradingAuthPolicies.OwnerOrService);

        // ---- 利用者のみ（FR-13・OwnerOnly）: 認可は owner サブグループに付与し親グループには付けない（親は 403）----
        var owner = g.MapGroup("").RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly);

        // ---- 監視設定（変動閾値・クールダウン・監視銘柄の一括置換。後方互換で従来どおり） ----
        owner.MapGet("/settings", (IMonitoredSymbolStore store) => Results.Ok(store.GetSettings()));

        owner.MapPut("/settings", (MonitorSettingsUpdateRequest req, IMonitoredSymbolStore store) =>
        {
            store.Save(req.ToSettings());
            return Results.Ok(store.GetSettings());
        });

        // ---- 監視銘柄（watchlist）の取得/追加/削除（FR-03/FR-13, UC-06, IADR-0088/0095）----
        // 追加/削除は理由必須（reason 空欄は 400）。actor は認証済みトークン名（preferred_username）から取る。
        // 重複追加・不在削除・空 symbol・未定義 market は 400、設定行の Version 楽観排他競合は 409（親の例外フィルタで写像）。
        // FR-02, IADR-0095: 取得は read（OwnerOrService）に置き、定時サイクル（#11 TradeDecision）が s2s 同期照会できるようにする。
        // 変更（追加/削除）と履歴は owner（OwnerOnly）据え置き＝変更は利用者のみ（FR-13）維持。
        read.MapGet("/watchlist", (MonitorWatchlistService svc) => Results.Ok(svc.GetWatchlist()));

        owner.MapPost("/watchlist", (WatchlistChangeRequest req, MonitorWatchlistService svc, HttpContext http) =>
            Results.Ok(svc.Add(req.Symbol, MarketOf(req), ActorOf(http), req.Reason)));

        // DELETE に body を持たせて理由を POST と対称に運ぶ（内部メッシュ限定 API・IADR-0088）。
        // DELETE は body 推論を許さないため [FromBody] を明示する。
        owner.MapDelete("/watchlist", ([FromBody] WatchlistChangeRequest req, MonitorWatchlistService svc, HttpContext http) =>
            Results.Ok(svc.Remove(req.Symbol, MarketOf(req), ActorOf(http), req.Reason)));

        owner.MapGet("/watchlist/history", (MonitorWatchlistService svc) => Results.Ok(svc.GetHistory()));

        return app;
    }

    // 認証済みトークンの名前（preferred_username）。OwnerOnly を通過している前提だが、null は unknown に倒す。
    private static string ActorOf(HttpContext http) =>
        http.User.Identity?.Name is { Length: > 0 } name ? name : "unknown";

    // FR-13: market の省略を検証エラー（400）にする。非 nullable enum だと省略時に既定値 Market.Japan(0) へ暗黙バインド
    // されるため、要求では nullable で受けて明示的な指定を必須にする。
    private static Market MarketOf(WatchlistChangeRequest req) =>
        req.Market ?? throw new ArgumentException("market は必須です。", nameof(req));
}

// 監視設定変更の要求。MonitoredSymbols は逆直列化可能な具象 List で受ける。
internal sealed record MonitorSettingsUpdateRequest(
    decimal MovementThresholdRatio,
    TimeSpan Cooldown,
    List<MonitoredSymbol> MonitoredSymbols)
{
    public MarketMonitorSettings ToSettings()
    {
        // FR-03: 不正値を弾く（閾値は正、クールダウンは非負）。
        if (MovementThresholdRatio <= 0m)
        {
            throw new ArgumentException("変動閾値は正の値である必要があります。", nameof(MovementThresholdRatio));
        }

        if (Cooldown < TimeSpan.Zero)
        {
            throw new ArgumentException("クールダウンは非負である必要があります。", nameof(Cooldown));
        }

        return new MarketMonitorSettings
        {
            MovementThresholdRatio = MovementThresholdRatio,
            Cooldown = Cooldown,
            MonitoredSymbols = MonitoredSymbols,
        };
    }
}

// FR-13, UC-06: 監視銘柄の追加/削除の要求（理由必須・FR-11）。actor は要求本文ではなく認証済みトークンから取る。
// Market は nullable で受け、省略（null）を 400 に弾く（非 nullable だと省略時に既定値 0 へ暗黙バインドされるため）。
internal sealed record WatchlistChangeRequest(string Symbol, Market? Market, string Reason);
