using ConfigurationService.Common.Exceptions;
using ConfigurationService.Features.Assumptions.GetAssumptions;
using ConfigurationService.Features.Assumptions.GetAssumptionsHistory;
using ConfigurationService.Features.Assumptions.UpdateAssumptions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace ConfigurationService.Features.Assumptions;

// FR-17, UC-06: 全体前提条件の照会・変更エンドポイント。actor は認証済みトークンの名前（preferred_username）。
//
// 認可（IADR-0063 決定 2・IADR-0051 の最小権限）: **読み取り（GET）のみ OwnerOrService** へ分離し、消費側サービス
// （費用統制 #139・損益集計・AI 判断）がサービストークン（trading-service）で共通参照できるようにする（IADR-0021 の
// 「単一の真実源を共通参照する」前提）。**更新（PUT）・履歴（GET /history）は OwnerOnly 据え置き**＝生成AI・自動処理は
// ロールを持たず変更できない（FR-17）。履歴は「誰がなぜ変えたか」の運用情報のためサービスへ開放しない。
//
// NFR, platform ADR-0068 決定1: **本ファイルは「登録表」である。** `MapGroup` ／ タグ ／ フィルタ ／
// `Program.cs` から呼ぶメソッド名（MapAssumptionsEndpoints）はここに残す。**個々の操作の処理は
// 3 段目（`<操作>/Endpoint.cs`）にある。** 登録の順序も動かさない。
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

        read.MapGetAssumptions();

        // ---- 利用者のみ（FR-17・OwnerOnly）: 更新・履歴。サービスには許可しない ----
        var owner = g.MapGroup("").RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly);

        owner.MapGetAssumptionsHistory();
        owner.MapUpdateAssumptions();

        return app;
    }
}
