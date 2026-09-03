using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.EntityFrameworkCore;
using MarketMonitorService.Features.MarketMonitor.AddWatchlistSymbol;
using MarketMonitorService.Features.MarketMonitor.GetMonitorSettings;
using MarketMonitorService.Features.MarketMonitor.GetMonitorSettingsHistory;
using MarketMonitorService.Features.MarketMonitor.GetWatchlist;
using MarketMonitorService.Features.MarketMonitor.GetWatchlistHistory;
using MarketMonitorService.Features.MarketMonitor.RemoveWatchlistSymbol;
using MarketMonitorService.Features.MarketMonitor.ReplaceMonitorSettings;
using MarketMonitorService.Features.MarketMonitor.UpdateCooldown;
using MarketMonitorService.Features.MarketMonitor.UpdateMovementThreshold;

namespace MarketMonitorService.Features.MarketMonitor;

// FR-03, FR-13, UC-06: 監視設定（監視銘柄・変動閾値・クールダウン）の照会・変更。
// 変更（PUT /settings・POST/DELETE /watchlist）と履歴は利用者のみ（OwnerOnly）＝生成AI・自動処理はこのロールを持たず変更できない。
// FR-02, IADR-0095: 監視銘柄の取得（GET /watchlist）のみサービス（trading-service）にも開放（OwnerOrService）＝定時サイクルが s2s 照会する。
// IADR-0088: 認可は read/owner サブグループに付与し、親グループには付けない（親は例外→HTTP 写像のみ）。Risk `/risk-controls` と同型。
//
// NFR, platform ADR-0068 決定1: **本ファイルは「登録表」である。** `MapGroup` ／ タグ ／ グループ単位の認可・
// フィルタ ／ `Program.cs` から呼ぶメソッド名（`MapMonitorSettingsEndpoints`）はここに残す —— これらは集約の全操作が
// 使うものであり、特定の 1 操作に属さない。**個々の操作の処理は 3 段目（`<操作>/Endpoint.cs`）にある。**
// 登録の順序も動かさない。
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

        owner.MapGetMonitorSettings();
        owner.MapReplaceMonitorSettings();

        // ---- 収集パラメータの部分更新（FR-03/FR-11/FR-13, UC-06, SC-01 §2, #340, IADR-0155）----
        owner.MapUpdateMovementThreshold();
        owner.MapUpdateCooldown();

        // 監視設定の変更履歴（監視銘柄・収集パラメータを 1 本の台帳で返す）。`/watchlist/history` は同じ台帳の
        // 別名であり、監視銘柄の文脈から辿るために残す（既存の消費者を壊さない）。
        owner.MapGetMonitorSettingsHistory();

        // ---- 監視銘柄（watchlist）の取得/追加/削除（FR-03/FR-13, UC-06, IADR-0088/0095）----
        // 追加/削除は理由必須（reason 空欄は 400）。actor は認証済みトークン名（preferred_username）から取る。
        // 重複追加・不在削除・空 symbol・未定義 market は 400、設定行の Version 楽観排他競合は 409（親の例外フィルタで写像）。
        // FR-02, IADR-0095: 取得は read（OwnerOrService）に置き、定時サイクル（#11 TradeDecision）が s2s 同期照会できるようにする。
        // 変更（追加/削除）と履歴は owner（OwnerOnly）据え置き＝変更は利用者のみ（FR-13）維持。
        read.MapGetWatchlist();
        owner.MapAddWatchlistSymbol();
        owner.MapRemoveWatchlistSymbol();
        owner.MapGetWatchlistHistory();

        return app;
    }

    // 認証済みトークンの名前（preferred_username）。OwnerOnly を通過している前提だが、null は unknown に倒す。
    // **2 段目に残る共通部分**（platform ADR-0068 決定3）——書き込み系の 5 操作が使う。
    internal static string ActorOf(HttpContext http) =>
        http.User.Identity?.Name is { Length: > 0 } name ? name : "unknown";

    // FR-13: market の省略を検証エラー（400）にする。非 nullable enum だと省略時に既定値 Market.Japan(0) へ暗黙バインド
    // されるため、要求では nullable で受けて明示的な指定を必須にする。
    // **2 段目に残る共通部分**——追加・削除の 2 操作が使う。
    internal static Market MarketOf(WatchlistChangeRequest req) =>
        req.Market ?? throw new ArgumentException("market は必須です。", nameof(req));
}
