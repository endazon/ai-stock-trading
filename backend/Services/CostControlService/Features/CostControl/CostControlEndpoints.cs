using CostControlService.Features.CostControl.GetCostReview;
using CostControlService.Features.CostControl.GetCostState;
using CostControlService.Features.CostControl.GetCostUsage;
using CostControlService.Features.CostControl.RecordCost;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CostControlService.Features.CostControl;

// NFR（費用）, FR-09, IADR-0027: 費用計上・統制判定・費用レビューのエンドポイント。
// しきい値の上方遷移時に CostThresholdReached を発行する。
//
// 認可（IADR-0051・最小権限）: 既定は OwnerOnly。**読み取り系（/state・/review）のみ OwnerOrService** へ分離し、
// 定時サイクル poller（情報収集 #9・IADR-0031）がサービストークンで統制状態を照会できるようにする。
// **書き込み系（/record）は OwnerOnly 据え置き**＝サービスへ費用の書き込み権限は与えない
// （サービスからの費用計上はイベント LlmCostIncurred で行う・IADR-0055 決定1）。
//
// NFR, platform ADR-0068 決定1: **本ファイルは「登録表」である。** `MapGroup` ／ タグ ／ フィルタ ／
// `Program.cs` から呼ぶメソッド名（MapCostControlEndpoints）はここに残す。**個々の操作の処理は
// 3 段目（`<操作>/Endpoint.cs`）にある。** 登録の順序も動かさない。
internal static class CostControlEndpoints
{
    public static IEndpointRouteBuilder MapCostControlEndpoints(this IEndpointRouteBuilder app)
    {
        // 認可は親では付けず read/owner のサブグループで指定する（親に付けると読み取り側で合成され
        // OwnerOnly も要求されてしまうため。RiskControlEndpoints/ReportEndpoints と同形）。
        var g = app.MapGroup("/costs")
            .WithTags("CostControl")
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
            });

        // ---- 書き込み系: 利用者のみ（OwnerOnly・最小権限） ----
        // サービスへ費用の書き込み権限は与えない（サービスからの計上はイベント LlmCostIncurred・IADR-0055 決定1）。
        var owner = g.MapGroup("").RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly);

        owner.MapRecordCost();

        // ---- 読み取り系: 利用者またはサービス（IADR-0051・OwnerOrService） ----
        // 定時サイクル poller（IADR-0031）が s2s（trading-service）で統制状態を照会できるよう分離する。
        var read = g.MapGroup("").RequireAuthorization(AiStockTradingAuthPolicies.OwnerOrService);

        read.MapGetCostState();
        read.MapGetCostReview();
        read.MapGetCostUsage();

        return app;
    }
}
