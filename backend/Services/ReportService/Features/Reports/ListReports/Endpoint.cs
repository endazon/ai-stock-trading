using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AppSvc = ReportService.Features.Reports.ReportAppService;

namespace ReportService.Features.Reports.ListReports;

// FR-06, UC-03〜05, ADR-0003: 報告書の一覧（利用者のみ）。
internal static class ListReportsEndpoint
{
    public static void MapListReports(this IEndpointRouteBuilder owner) =>
        owner.MapGet("", (AppSvc svc) => Results.Ok(svc.List()))
            // NFR-06, #1554, IADR-0622: ボットの許可表の経路（ボットの REST アダプタが呼ぶ）。
            .AllowOwnerBot();
}
