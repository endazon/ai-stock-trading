using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace ConfigurationService.Features.Assumptions.GetAssumptions;

// FR-17, UC-06: 現在の前提条件とバージョン（報告書は生成時 version を凍結参照でき、消費側は共通参照できる）。
internal static class GetAssumptionsEndpoint
{
    public static void MapGetAssumptions(this IEndpointRouteBuilder read) =>
        read.MapGet("", (AssumptionsService svc) => Results.Ok(svc.GetCurrent()));
}
