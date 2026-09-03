using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using AppSvc = CostControlService.Features.CostControl.CostControlAppService;

namespace CostControlService.Features.CostControl.GetCostReview;

// NFR（費用）, FR-16, IADR-0027: 現在月の費用÷資金比率（月報の費用レビュー）。
internal static class GetCostReviewEndpoint
{
    public static void MapGetCostReview(this IEndpointRouteBuilder read) =>
        read.MapGet("/review", (AppSvc svc, decimal capital) => Results.Ok(new { ratio = svc.Review(capital) }));
}
