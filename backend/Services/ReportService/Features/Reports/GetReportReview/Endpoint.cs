using AppSvc = ReportService.Features.Reports.ReportAppService;

namespace ReportService.Features.Reports.GetReportReview;

internal static class GetReportReviewEndpoint
{
    // 現在のレビュー局面（状態＋版番号）。Bot/UI が次操作の期待版を得るために照会する。
    public static void MapGetReportReview(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/{periodKey}/review", (string periodKey, AppSvc svc) =>
        {
            var review = svc.GetReview(periodKey);
            return review is null ? Results.NotFound() : Results.Ok(review);
        });
}
