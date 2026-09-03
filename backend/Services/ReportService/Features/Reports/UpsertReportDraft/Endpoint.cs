using ReportService.Domain;
using AppSvc = ReportService.Features.Reports.ReportAppService;

namespace ReportService.Features.Reports.UpsertReportDraft;

internal static class UpsertReportDraftEndpoint
{
    // ドラフトの作成/更新（利用者のみ・楽観排他）。
    public static void MapUpsertReportDraft(this IEndpointRouteBuilder owner) =>
        owner.MapPut("/{periodKey}", (string periodKey, UpsertReportRequest req, AppSvc svc) =>
        {
            var report = new TradingReport
            {
                PeriodKey = periodKey,
                Kind = req.Kind,
                PeriodStart = req.PeriodStart,
                BasedOn = req.BasedOn,
                AssumptionsVersion = req.AssumptionsVersion,
                PolicySummary = req.PolicySummary,
            };
            var version = svc.UpsertDraft(report, req.ExpectedVersion);
            return Results.Ok(new { periodKey, version });
        });
}

// ドラフト upsert の要求。TradingReport は具象レコードのため標準の逆直列化が可能。
internal sealed record UpsertReportRequest(
    ReportKind Kind,
    DateOnly PeriodStart,
    string? BasedOn,
    int AssumptionsVersion,
    string PolicySummary,
    int ExpectedVersion);
