using AppSvc = ReportService.Features.Reports.ReportAppService;

namespace ReportService.Features.Reports.GetReport;

// FR-06, UC-03〜05, ADR-0003: 報告書 1 件の照会（利用者のみ）。対象が無ければ 404。
internal static class GetReportEndpoint
{
    public static void MapGetReport(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/{periodKey}", (string periodKey, AppSvc svc) =>
        {
            var report = svc.Get(periodKey);
            return report is null ? Results.NotFound() : Results.Ok(report);
        });
}
