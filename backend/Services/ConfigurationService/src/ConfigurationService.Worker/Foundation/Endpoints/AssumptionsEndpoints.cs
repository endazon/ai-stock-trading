using AiStockTrading.Configuration.Application;
using AiStockTrading.Configuration.Application.Ports;
using AiStockTrading.Configuration.Application.Services;
using AiStockTrading.Configuration.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.Configuration.Worker.Foundation.Endpoints;

// FR-17, UC-06, ADR-0007: 全体前提条件の照会・変更エンドポイント。すべて OwnerOnly（利用者のみ・Keycloak trading-owner）を
// 要求する。actor は認証済みトークンの名前（preferred_username）。生成AI・自動処理はこのロールを持たないため変更できない。
internal static class AssumptionsEndpoints
{
    public static IEndpointRouteBuilder MapAssumptionsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/assumptions")
            .RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly)
            .WithTags("Assumptions")
            // 例外→HTTP マッピング: アクター/理由欠如などの検証失敗は 400、楽観排他の競合は 409。
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
                catch (AssumptionsConcurrencyException e)
                {
                    return Results.Conflict(new { error = e.Message });
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Results.Conflict(new { error = "前提条件が他の更新と競合しました。最新を取得して再試行してください。" });
                }
            });

        // 現在の前提条件とバージョン（報告書は生成時 version を凍結参照できる）。
        g.MapGet("", (AssumptionsService svc) => Results.Ok(svc.GetCurrent()));

        // 変更履歴（新しい順）。
        g.MapGet("/history", (AssumptionsService svc) => Results.Ok(svc.GetHistory()));

        // 前提条件の更新（利用者のみ・理由必須・楽観排他）。成功時に AssumptionsChanged を発行し通知サービスへ伝える。
        g.MapPut("", async (UpdateAssumptionsRequest req, AssumptionsService svc, IPublishEndpoint bus, IClock clock, HttpContext http) =>
        {
            var actor = ActorOf(http);
            var version = svc.Update(req.Assumptions, req.ExpectedVersion, actor, req.Reason);
            await bus.Publish(new AssumptionsChanged(version, actor, req.Reason, clock.UtcNow));
            return Results.Ok(svc.GetCurrent());
        });

        return app;
    }

    private static string ActorOf(HttpContext http) =>
        http.User.Identity?.Name is { Length: > 0 } name ? name : "unknown";
}

// 前提条件更新の要求。TradingAssumptions は具象レコードのため標準の逆直列化が可能。ExpectedVersion で楽観排他する。
internal sealed record UpdateAssumptionsRequest(TradingAssumptions Assumptions, int ExpectedVersion, string Reason);
