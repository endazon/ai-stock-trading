using ReportService.Domain;
using AppSvc = ReportService.Features.Reports.ReportAppService;

namespace ReportService.Features.Reports.PresentReport;

internal static class PresentReportEndpoint
{
    // 提示（Drafting/ChangesRequested→PendingApproval）。内容不変のため版番号は変わらない。
    public static void MapPresentReport(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/{periodKey}/present", (string periodKey, ReviewCommandRequest req, AppSvc svc, HttpContext http) =>
            ReportEndpoints.ReviewResult(svc.ApplyReview(periodKey, ReviewAction.Present, req.ExpectedVersion, ReportEndpoints.ActorOf(http))));
}
