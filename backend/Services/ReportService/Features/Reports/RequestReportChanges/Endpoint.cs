using ReportService.Domain;
using AppSvc = ReportService.Features.Reports.ReportAppService;

namespace ReportService.Features.Reports.RequestReportChanges;

internal static class RequestReportChangesEndpoint
{
    // 差し戻し（PendingApproval→ChangesRequested）。修正指示を受けて改訂を促す。内容不変のため版番号は変わらない。
    public static void MapRequestReportChanges(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/{periodKey}/request-changes", (string periodKey, ReviewCommandRequest req, AppSvc svc, HttpContext http) =>
            ReportEndpoints.ReviewResult(svc.ApplyReview(periodKey, ReviewAction.RequestChanges, req.ExpectedVersion, ReportEndpoints.ActorOf(http))));
}
