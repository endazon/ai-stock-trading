using AiStockTrading.Report.Application;
using AiStockTrading.Report.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using AppSvc = AiStockTrading.Report.Application.Services.ReportService;

namespace AiStockTrading.Report.Worker.Foundation.Endpoints;

// FR-06/07, UC-03〜05, ADR-0007: 報告書のドラフト管理・確定・照会エンドポイント。確定は OwnerOnly（利用者のみ・Keycloak
// trading-owner）。生成AI・自動処理は確定できない（ADR-0003/0007）。確定の遷移時に ReportConfirmed を発行する。
internal static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/reports")
            .RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly)
            .WithTags("Reports")
            // 例外→HTTP マッピング: 検証失敗は 400、確定済み変更・楽観排他競合は 409。
            .AddEndpointFilter(async (ctx, next) =>
            {
                try
                {
                    return await next(ctx);
                }
                catch (ArgumentException e)
                {
                    return Results.BadRequest(new { error = e.Message });
                }
                catch (ReportConcurrencyException e)
                {
                    return Results.Conflict(new { error = e.Message });
                }
                catch (InvalidOperationException e)
                {
                    return Results.Conflict(new { error = e.Message });
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Results.Conflict(new { error = "報告書が他の更新と競合しました。最新を取得して再試行してください。" });
                }
            });

        g.MapGet("", (AppSvc svc) => Results.Ok(svc.List()));

        // 確定済み日報の方針（取引判断の実データ源）。未確定なら 404。/{periodKey} より優先される（リテラル一致）。
        g.MapGet("/daily-policy", (AppSvc svc) =>
        {
            var policy = svc.GetConfirmedDailyPolicy();
            return policy is null ? Results.NotFound() : Results.Ok(policy);
        });

        g.MapGet("/{periodKey}", (string periodKey, AppSvc svc) =>
        {
            var report = svc.Get(periodKey);
            return report is null ? Results.NotFound() : Results.Ok(report);
        });

        // ドラフトの作成/更新（利用者のみ・楽観排他）。
        g.MapPut("/{periodKey}", (string periodKey, UpsertReportRequest req, AppSvc svc) =>
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

        // 確定（Draft→Confirmed・利用者のみ・版番号付き冪等）。遷移時のみ ReportConfirmed を発行。対象が無ければ 404。
        g.MapPost("/{periodKey}/confirm", async (string periodKey, ConfirmReportRequest req, AppSvc svc, IPublishEndpoint bus, HttpContext http) =>
        {
            var actor = ActorOf(http);
            var result = svc.Confirm(periodKey, req.ExpectedVersion, actor);
            if (result is null)
                return Results.NotFound();

            if (result.Transitioned)
            {
                var r = result.Report;
                await bus.Publish(new ReportConfirmed(
                    r.PeriodKey, r.Kind.ToString(), actor, r.AssumptionsVersion, r.ConfirmedAt ?? DateTimeOffset.UtcNow));
            }

            return Results.Ok(result.Report);
        });

        return app;
    }

    private static string ActorOf(HttpContext http) =>
        http.User.Identity?.Name is { Length: > 0 } name ? name : "unknown";
}

// ドラフト upsert の要求。TradingReport は具象レコードのため標準の逆直列化が可能。
internal sealed record UpsertReportRequest(
    ReportKind Kind,
    DateOnly PeriodStart,
    string? BasedOn,
    int AssumptionsVersion,
    string PolicySummary,
    int ExpectedVersion);

// 確定の要求（版番号付き冪等）。
internal sealed record ConfirmReportRequest(int ExpectedVersion);
