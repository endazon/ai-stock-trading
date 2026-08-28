using ConfigurationService.Application;
using ConfigurationService.Application.Ports;
using ConfigurationService.Application.Services;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Kernel.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace ConfigurationService.Api.Endpoints;

// FR-17, UC-06: 全体前提条件の照会・変更エンドポイント。actor は認証済みトークンの名前（preferred_username）。
//
// 認可（IADR-0063 決定 2・IADR-0051 の最小権限）: **読み取り（GET）のみ OwnerOrService** へ分離し、消費側サービス
// （費用統制 #139・損益集計・AI 判断）がサービストークン（trading-service）で共通参照できるようにする（IADR-0021 の
// 「単一の真実源を共通参照する」前提）。**更新（PUT）・履歴（GET /history）は OwnerOnly 据え置き**＝生成AI・自動処理は
// ロールを持たず変更できない（FR-17）。履歴は「誰がなぜ変えたか」の運用情報のためサービスへ開放しない。
internal static class AssumptionsEndpoints
{
    public static IEndpointRouteBuilder MapAssumptionsEndpoints(this IEndpointRouteBuilder app)
    {
        // 認可は親では付けず read/owner のサブグループで指定する（親に付けると読み取り側で合成され OwnerOnly も
        // 要求されてしまい、サービストークンが 403 になる。CostControlEndpoints/ReportEndpoints と同形）。
        var g = app.MapGroup("/assumptions")
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

        // ---- 読み取り系: 利用者またはサービス（IADR-0063 決定 2・OwnerOrService） ----
        var read = g.MapGroup("").RequireAuthorization(AiStockTradingAuthPolicies.OwnerOrService);

        // 現在の前提条件とバージョン（報告書は生成時 version を凍結参照でき、消費側は共通参照できる）。
        read.MapGet("", (AssumptionsService svc) => Results.Ok(svc.GetCurrent()));

        // ---- 利用者のみ（FR-17・OwnerOnly）: 更新・履歴。サービスには許可しない ----
        var owner = g.MapGroup("").RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly);

        // 変更履歴（新しい順）。
        owner.MapGet("/history", (AssumptionsService svc) => Results.Ok(svc.GetHistory()));

        // 前提条件の更新（利用者のみ・理由必須・楽観排他）。成功時に AssumptionsChanged を発行し通知サービスへ伝える。
        owner.MapPut("", async (UpdateAssumptionsRequest req, AssumptionsService svc, IMessageBus bus, IClock clock, HttpContext http) =>
        {
            var actor = ActorOf(http);
            var version = svc.Update(req.Assumptions, req.ExpectedVersion, actor, req.Reason);
            await bus.PublishAsync(new AssumptionsChanged(version, actor, req.Reason, clock.UtcNow));
            return Results.Ok(svc.GetCurrent());
        });

        return app;
    }

    private static string ActorOf(HttpContext http) =>
        http.User.Identity?.Name is { Length: > 0 } name ? name : "unknown";
}

// 前提条件更新の要求。TradingAssumptions は具象レコードのため標準の逆直列化が可能。ExpectedVersion で楽観排他する。
internal sealed record UpdateAssumptionsRequest(TradingAssumptions Assumptions, int ExpectedVersion, string Reason);
