using AppSvc = ReportService.Features.Reports.ReportAppService;

namespace ReportService.Features.Reports.GetConfirmedDailyPolicy;

// FR-06/07, UC-03〜05, ADR-0003, IADR-0051: 確定済み日報の方針（取引判断#11 の実データ源）。
internal static class GetConfirmedDailyPolicyEndpoint
{
    // 確定済み日報の方針。未確定なら 404。/{periodKey} より優先される（リテラル一致）。
    public static void MapGetConfirmedDailyPolicy(this IEndpointRouteBuilder read) =>
        read.MapGet("/daily-policy", (AppSvc svc) =>
        {
            var policy = svc.GetConfirmedDailyPolicy();
            return policy is null ? Results.NotFound() : Results.Ok(policy);
        });
}
