using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Logging;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using ReportService.Infrastructure.ExternalServices;
using Wolverine;
using AppSvc = ReportService.Features.Reports.ReportAppService;

namespace ReportService.Features.Reports.ConfirmReport;

internal static class ConfirmReportEndpoint
{
    // 確定（Draft→Confirmed・利用者のみ・版番号付き冪等）。遷移時のみ ReportConfirmed を発行し、確定報告書を KB へ保存する。
    // 対象が無ければ 404。KB 保存は best-effort（既定 no-op・fail-safe＝確定を壊さない・FR-08/IADR-0071 決定3）。
    //
    // FR-09, UC-03, ADR-0003, IADR-0240 決定11, #774: **確定者は ConfirmingActorResolver が決める。** Discord Bot は
    // owner マップ機密クライアントのトークンで呼ぶため、トークンの主体は人ではない。本文の OnBehalfOf（代理される
    // 利用者）は**信頼するクライアントのトークンに限って**採り、利用者トークン直叩きでは無視する（なりすまし防止）。
    public static void MapConfirmReport(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/{periodKey}/confirm", async (string periodKey, ConfirmReportRequest req, AppSvc svc,
            IMessageBus bus, IKnowledgeBaseWriter kb, ILoggerFactory loggerFactory, DelegatedActorOptions delegated,
            HttpContext http) =>
        {
            var confirming = ConfirmingActorResolver.Resolve(http.User, req.OnBehalfOf, delegated.TrustedClientIds);
            var actorLogger = loggerFactory.CreateLogger("ReportConfirmingActor");

            // 信頼クライアントの代理指定が値域外。**確定者を記録できない確定は行わない**（状態にも触れない）。
            if (confirming.Rejected)
            {
                actorLogger.LogWarning(
                    "確定要求の代理される利用者（OnBehalfOf）が値域外のため確定を拒否しました（PeriodKey={PeriodKey}）。",
                    LogSanitizer.Sanitize(periodKey));
                return Results.BadRequest(new { error = "代理される利用者（onBehalfOf）の形式が不正です。" });
            }

            // 代理指定を信じなかった（利用者トークン直叩き・一覧外のクライアント・一覧未設定）。確定は通すが、
            // なりすましの試行／設定漏れのどちらも見えるよう警告に残す。
            if (confirming.IgnoredOnBehalfOf)
            {
                actorLogger.LogWarning(
                    "確定要求の OnBehalfOf を無視しました（信頼するクライアントのトークンではありません。"
                    + "PeriodKey={PeriodKey}・確定者={Actor}）。",
                    LogSanitizer.Sanitize(periodKey), LogSanitizer.Sanitize(confirming.Actor));
            }

            var actor = confirming.Actor;
            var result = svc.Confirm(periodKey, req.ExpectedVersion, actor);
            if (result is null)
                return Results.NotFound();

            if (result.Transitioned)
            {
                var r = result.Report;
                await bus.PublishAsync(new ReportConfirmed(
                    r.PeriodKey, r.Kind.ToString(), actor, r.AssumptionsVersion, r.ConfirmedAt ?? DateTimeOffset.UtcNow,
                    confirming.AuthorizedBy));

                // FR-08, IADR-0069/0071 決定3, #565, IADR-0274: 確定報告書を KB へ保存（本文つき。既定 no-op）。
                // 保存の失敗・例外は握りつぶし確定を壊さない（KB は best-effort・保存ポート自体も fail-safe）。
                var kbLogger = loggerFactory.CreateLogger("ReportKnowledgeBase");
                try
                {
                    await kb.SaveAsync(ReportKnowledgeMapper.ToDocument(r, kbLogger), http.RequestAborted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    kbLogger.LogWarning(ex, "確定報告書 {PeriodKey} の KB 保存に失敗しました（確定は継続）。", r.PeriodKey);
                }
            }

            return Results.Ok(result.Report);
        });
}

// 確定の要求（版番号付き冪等）。
// OnBehalfOf（任意・末尾に追加）: 呼び出し元が**代理している利用者**（Keycloak 利用者名）。Discord Bot が載せる。
// 信頼するクライアントのトークン以外では無視される（ConfirmingActorResolver）。
internal sealed record ConfirmReportRequest(int ExpectedVersion, string? OnBehalfOf = null);
