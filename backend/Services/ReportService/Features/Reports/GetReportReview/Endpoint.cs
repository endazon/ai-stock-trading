using AppSvc = ReportService.Features.Reports.ReportAppService;

namespace ReportService.Features.Reports.GetReportReview;

internal static class GetReportReviewEndpoint
{
    // 現在のレビュー局面（状態＋版番号）。Bot/UI が次操作の期待版を得るために照会する。
    public static void MapGetReportReview(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/{periodKey}/review", (string periodKey, AppSvc svc) =>
        {
            // #840, IADR-0352 決定 5: 従来の 3 項目（periodKey / state / version）に unsuppliedInputs を**足す**
            // （既存の読み手は増えた項目を無視する＝非破壊）。
            var review = svc.GetReviewView(periodKey);
            return review is null ? Results.NotFound() : Results.Ok(review);
        });
}
