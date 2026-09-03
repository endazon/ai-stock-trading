using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using ReportService.Infrastructure.ExternalServices;
using Wolverine;
using AppSvc = ReportService.Features.Reports.ReportAppService;

namespace ReportService.Features.Reports.ConfirmReport;

internal static class ConfirmReportEndpoint
{
    // 確定（Draft→Confirmed・利用者のみ・版番号付き冪等）。遷移時のみ ReportConfirmed を発行し、確定報告書を KB へ保存する。
    // 対象が無ければ 404。KB 保存は best-effort（既定 no-op・fail-safe＝確定を壊さない・FR-08/IADR-0071 決定3）。
    public static void MapConfirmReport(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/{periodKey}/confirm", async (string periodKey, ConfirmReportRequest req, AppSvc svc,
            IMessageBus bus, IKnowledgeBaseWriter kb, ILoggerFactory loggerFactory, HttpContext http) =>
        {
            var actor = ReportEndpoints.ActorOf(http);
            var result = svc.Confirm(periodKey, req.ExpectedVersion, actor);
            if (result is null)
                return Results.NotFound();

            if (result.Transitioned)
            {
                var r = result.Report;
                await bus.PublishAsync(new ReportConfirmed(
                    r.PeriodKey, r.Kind.ToString(), actor, r.AssumptionsVersion, r.ConfirmedAt ?? DateTimeOffset.UtcNow));

                // FR-08, IADR-0069/0071 決定3: 確定報告書を KB へ保存（カタログ登録）。既定 no-op。
                // 保存の失敗・例外は握りつぶし確定を壊さない（KB は best-effort・保存ポート自体も fail-safe）。
                try
                {
                    await kb.SaveAsync(ReportKnowledgeMapper.ToDocument(r), http.RequestAborted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    loggerFactory.CreateLogger("ReportKnowledgeBase")
                        .LogWarning(ex, "確定報告書 {PeriodKey} の KB 保存に失敗しました（確定は継続）。", r.PeriodKey);
                }
            }

            return Results.Ok(result.Report);
        });
}

// 確定の要求（版番号付き冪等）。
internal sealed record ConfirmReportRequest(int ExpectedVersion);
