using ReportService.Domain;
using AppSvc = ReportService.Features.Reports.ReportAppService;

namespace ReportService.Features.Reports.PresentReport;

internal static class PresentReportEndpoint
{
    // 提示（Drafting/ChangesRequested→PendingApproval）。内容不変のため版番号は変わらない。
    public static void MapPresentReport(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/{periodKey}/present", async (string periodKey, ReviewCommandRequest req, AppSvc svc,
            IReportDraftKnowledgeCopy draftCopy, HttpContext http) =>
        {
            var decision = svc.ApplyReview(periodKey, ReviewAction.Present, req.ExpectedVersion, ReportEndpoints.ActorOf(http));

            // FR-06, FR-08, UC-03, #1300, IADR-0526 決定 2: 手で承認待ちにした版も写し（ドラフト）を持つ（遷移したときだけ。冪等な再提示では送らない）。
            // 手の経路の本文は空なので、写しには方針を載せる。best-effort（ポートは例外を投げない）。取り消しを渡さない（提示は済んでいる）。
            if (decision is { Accepted: true, Transitioned: true, Review.State: ReviewState.PendingApproval }
                && svc.Get(periodKey) is { } current)
                await draftCopy.PublishAsync(current.Report, decision.Review.Version, CancellationToken.None);

            return ReportEndpoints.ReviewResult(decision);
        });
}
