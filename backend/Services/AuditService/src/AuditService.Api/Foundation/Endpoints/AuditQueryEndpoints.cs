using AiStockTrading.Audit.Application.Ports;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AiStockTrading.Audit.Api.Foundation.Endpoints;

// FR-11, UC-07, IADR-0019: 監査台帳の照会エンドポイント。監査は取引履歴＝機微情報のため、すべて OwnerOnly
// （利用者のみ・Keycloak ロール trading-owner）を要求する（RiskControl と同じ認可方針）。
internal static class AuditQueryEndpoints
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    public static IEndpointRouteBuilder MapAuditQueryEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/audit")
            .RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly)
            .WithTags("Audit");

        // 注文単位（相関）の全記録を時系列（昇順）で返す＝「いつ・何を根拠に・何をしたか」を辿る。
        g.MapGet("/events/{correlationId:guid}", (Guid correlationId, IAuditEventStore store) =>
            Results.Ok(store.GetByCorrelation(correlationId)));

        // 直近の記録（降順）。limit は 1〜500 にクランプ（既定 100）。
        g.MapGet("/events", (IAuditEventStore store, int? limit) =>
            Results.Ok(store.GetRecent(Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit))));

        return app;
    }
}
