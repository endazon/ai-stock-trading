using AiStockTrading.CostControl.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;
using AppSvc = AiStockTrading.CostControl.Application.Services.CostControlService;

namespace AiStockTrading.CostControl.Api.Foundation.Endpoints;

// NFR（費用）, FR-09, IADR-0027: 費用計上・統制判定・費用レビューのエンドポイント。
// しきい値の上方遷移時に CostThresholdReached を発行する。
//
// 認可（IADR-0051・最小権限）: 既定は OwnerOnly。**読み取り系（/state・/review）のみ OwnerOrService** へ分離し、
// 定時サイクル poller（情報収集 #9・IADR-0031）がサービストークンで統制状態を照会できるようにする。
// **書き込み系（/record）は OwnerOnly 据え置き**＝サービスへ費用の書き込み権限は与えない
// （サービスからの費用計上はイベント LlmCostIncurred で行う・IADR-0055 決定1）。
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

        // 費用を計上する。統制状態が上方に遷移したら CostThresholdReached を発行する。
        owner.MapPost("/record", async (RecordCostRequest req, AppSvc svc, IMessageBus bus, CancellationToken ct) =>
        {
            var result = await svc.RecordAsync(req.Category, req.Amount, ct);

            if (result.CrossedTo is { } crossed)
            {
                await bus.PublishAsync(new CostThresholdReached(
                    result.Month, req.Category.ToString(), result.Percent, crossed.ToString(), DateTimeOffset.UtcNow));
            }

            return Results.Ok(new { result.Decision.State, result.Decision.IntervalMultiplier, result.Percent, result.Month });
        });

        // ---- 読み取り系: 利用者またはサービス（IADR-0051・OwnerOrService） ----
        // 定時サイクル poller（IADR-0031）が s2s（trading-service）で統制状態を照会できるよう分離する。
        var read = g.MapGroup("").RequireAuthorization(AiStockTradingAuthPolicies.OwnerOrService);

        // 現在月の LLM 統制判定（定時サイクルが間隔延長/停止を照会する）。
        read.MapGet("/state", async (AppSvc svc, CancellationToken ct) => Results.Ok(await svc.GetLlmStateAsync(ct)));

        // 現在月の費用÷資金比率（月報の費用レビュー）。
        read.MapGet("/review", (AppSvc svc, decimal capital) => Results.Ok(new { ratio = svc.Review(capital) }));

        // NFR（費用）, FR-16, 05_trading-assumptions §6.1, #347: 現在月の費用実績（カテゴリ別内訳）。
        // 月報の「当月の LLM 利用実績」の供給元。**上限の対象外（LlmUncapped）も返す**——抑制はしないが記載はする。
        read.MapGet("/usage", async (AppSvc svc, CancellationToken ct) => Results.Ok(await svc.GetUsageAsync(ct)));

        return app;
    }
}

// 費用計上の要求（カテゴリ・金額[円]）。
internal sealed record RecordCostRequest(CostCategory Category, decimal Amount);
