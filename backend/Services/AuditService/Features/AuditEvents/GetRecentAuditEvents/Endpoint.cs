using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AuditService.Features.AuditEvents.GetRecentAuditEvents;

// FR-11, UC-07, IADR-0019: 直近の記録（降順）。limit は 1〜500 にクランプ（既定 100）。
internal static class GetRecentAuditEventsEndpoint
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    public static void MapGetRecentAuditEvents(this IEndpointRouteBuilder g) =>
        g.MapGet("/events", (IAuditEventStore store, int? limit) =>
                Results.Ok(store.GetRecent(Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit))))
            .RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly);
}
