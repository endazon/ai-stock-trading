using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AuditService.Features.AuditEvents.GetAuditEventsByCorrelation;

// FR-11, UC-07, IADR-0019: 注文単位（相関）の全記録を時系列（昇順）で返す＝「いつ・何を根拠に・何をしたか」を辿る。
internal static class GetAuditEventsByCorrelationEndpoint
{
    public static void MapGetAuditEventsByCorrelation(this IEndpointRouteBuilder g) =>
        g.MapGet("/events/{correlationId:guid}", (Guid correlationId, IAuditEventStore store) =>
                Results.Ok(store.GetByCorrelation(correlationId)))
            .RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly);
}
