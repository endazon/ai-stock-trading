using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using AppSvc = CostControlService.Features.CostControl.CostControlAppService;

namespace CostControlService.Features.CostControl.GetCostUsage;

// NFR（費用）, FR-16, 05_trading-assumptions §6.1, #347: 現在月の費用実績（カテゴリ別内訳）。
// 月報の「当月の LLM 利用実績」の供給元。**上限の対象外（LlmUncapped）も返す**——抑制はしないが記載はする。
internal static class GetCostUsageEndpoint
{
    public static void MapGetCostUsage(this IEndpointRouteBuilder read) =>
        read.MapGet("/usage", async (AppSvc svc, CancellationToken ct) => Results.Ok(await svc.GetUsageAsync(ct)));
}
