using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using AppSvc = CostControlService.Features.CostControl.CostControlAppService;

namespace CostControlService.Features.CostControl.GetCostState;

// NFR（費用）, IADR-0027: 現在月の LLM 統制判定（定時サイクルが間隔延長/停止を照会する）。
internal static class GetCostStateEndpoint
{
    public static void MapGetCostState(this IEndpointRouteBuilder read) =>
        read.MapGet("/state", async (AppSvc svc, CancellationToken ct) => Results.Ok(await svc.GetLlmStateAsync(ct)));
}
