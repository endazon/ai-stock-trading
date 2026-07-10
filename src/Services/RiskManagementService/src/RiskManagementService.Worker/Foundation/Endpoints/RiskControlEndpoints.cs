using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.RiskManagement.Worker.Foundation.Endpoints;

// FR-10, FR-19, FR-20, UC-06, ADR-0007: kill switch 操作・リスク設定変更の HTTP エンドポイント。
// すべて OwnerOnly（利用者のみ・Keycloak ロール trading-owner）を要求する。actor は認証済みトークンの名前
// （preferred_username）を用いる。生成AI・自動処理はこのロールを持たないため変更できない。
internal static class RiskControlEndpoints
{
    public static IEndpointRouteBuilder MapRiskControlEndpoints(this IEndpointRouteBuilder app)
    {
        // 利用者のみ（ADR-0007）。未認証は 401、ロール無しは 403。
        var g = app.MapGroup("/risk-controls")
            .RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly)
            .WithTags("RiskControls")
            // 例外→HTTP マッピング: アクター/理由欠如などの検証失敗は 400、設定の楽観排他競合（IADR-0012）は 409。
            // これらを既定の 500 にせず、クライアントが区別できるステータスに写像する。
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

        // ---- サイジング文脈（FR-04/10, IADR-0029） ----
        // 取引判断（#11）が同期照会するサイジング文脈（設定＋ポートフォリオ状態から導出）。
        g.MapGet("/sizing-context", (SizingContextService svc) => Results.Ok(svc.Build()));

        // ---- kill switch（FR-10, ADR-0003） ----
        g.MapGet("/kill-switch", (KillSwitchService svc) => Results.Ok(svc.GetState()));

        g.MapPost("/kill-switch/engage", (KillSwitchRequest req, KillSwitchService svc, HttpContext http) =>
        {
            svc.Engage(ActorOf(http), req.Reason);
            return Results.Ok(svc.GetState());
        });

        g.MapPost("/kill-switch/disengage", (KillSwitchRequest req, KillSwitchService svc, HttpContext http) =>
        {
            svc.Disengage(ActorOf(http), req.Reason);
            return Results.Ok(svc.GetState());
        });

        // ---- 設定（FR-10/FR-19/FR-20, ADR-0007） ----
        g.MapGet("/settings", (RiskSettingsService svc) => Results.Ok(svc.GetCurrent()));

        g.MapGet("/settings/history", (ISettingsChangeLog changeLog) => Results.Ok(changeLog.GetHistory()));

        g.MapPut("/settings/limits", (LimitsUpdateRequest req, RiskSettingsService svc, HttpContext http) =>
        {
            svc.UpdateLimits(req.Limits, ActorOf(http), req.Reason);
            return Results.Ok(svc.GetCurrent());
        });

        g.MapPut("/settings/stage", (StageUpdateRequest req, RiskSettingsService svc, HttpContext http) =>
        {
            svc.UpdateStage(req.Stage, ActorOf(http), req.Reason);
            return Results.Ok(svc.GetCurrent());
        });

        g.MapPut("/settings/guard", (GuardUpdateRequest req, RiskSettingsService svc, HttpContext http) =>
        {
            svc.UpdateGuard(req.ToGuardSettings(), ActorOf(http), req.Reason);
            return Results.Ok(svc.GetCurrent());
        });

        return app;
    }

    // 認証済みトークンの名前（preferred_username）。OwnerOnly を通過している前提だが、null は unknown に倒す。
    private static string ActorOf(HttpContext http) =>
        http.User.Identity?.Name is { Length: > 0 } name ? name : "unknown";
}

// kill switch 操作の要求（理由必須・ADR-0007）。
internal sealed record KillSwitchRequest(string Reason);

// 上限変更の要求。RiskLimitSettings は具象プロパティのレコードで標準の逆直列化が可能。
internal sealed record LimitsUpdateRequest(RiskLimitSettings Limits, string Reason);

// 段階変更の要求。
internal sealed record StageUpdateRequest(StageSettings Stage, string Reason);

// ガード変更の要求。TradingGuardSettings は IReadOnlySet 等を用いるため、逆直列化可能な具象コレクションで受ける。
internal sealed record GuardUpdateRequest(
    List<ProductType> EnabledProductTypes,
    List<Market> EnabledMarkets,
    List<BannedSymbol> BannedSymbols,
    bool PreventSameDayReentry,
    bool ProhibitManipulativeOrderPatterns,
    string Reason)
{
    public TradingGuardSettings ToGuardSettings() => new()
    {
        EnabledProductTypes = new HashSet<ProductType>(EnabledProductTypes),
        EnabledMarkets = new HashSet<Market>(EnabledMarkets),
        BannedSymbols = BannedSymbols,
        PreventSameDayReentry = PreventSameDayReentry,
        ProhibitManipulativeOrderPatterns = ProhibitManipulativeOrderPatterns,
    };
}
