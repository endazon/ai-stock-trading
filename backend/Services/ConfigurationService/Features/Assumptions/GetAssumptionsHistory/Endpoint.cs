using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace ConfigurationService.Features.Assumptions.GetAssumptionsHistory;

// FR-17, UC-06: 変更履歴（新しい順）。利用者のみ（OwnerOnly）。
internal static class GetAssumptionsHistoryEndpoint
{
    public static void MapGetAssumptionsHistory(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/history", (AssumptionsService svc) => Results.Ok(svc.GetHistory()));
}
