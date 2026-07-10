using AiStockTrading.CostControl.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using AppSvc = AiStockTrading.CostControl.Application.Services.CostControlService;

namespace AiStockTrading.CostControl.Worker.Foundation.Endpoints;

// NFR（費用）, FR-09, IADR-0027: 費用計上・統制判定・費用レビューのエンドポイント。すべて OwnerOnly（利用者/サービスの
// 認可は #22 で service-to-service に調整）。しきい値の上方遷移時に CostThresholdReached を発行する。
internal static class CostControlEndpoints
{
    public static IEndpointRouteBuilder MapCostControlEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/costs")
            .RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly)
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

        // 費用を計上する。統制状態が上方に遷移したら CostThresholdReached を発行する。
        g.MapPost("/record", async (RecordCostRequest req, AppSvc svc, IPublishEndpoint bus) =>
        {
            var result = svc.Record(req.Category, req.Amount);

            if (result.CrossedTo is { } crossed)
            {
                await bus.Publish(new CostThresholdReached(
                    result.Month, req.Category.ToString(), result.Percent, crossed.ToString(), DateTimeOffset.UtcNow));
            }

            return Results.Ok(new { result.Decision.State, result.Decision.IntervalMultiplier, result.Percent, result.Month });
        });

        // 現在月の LLM 統制判定（定時サイクルが間隔延長/停止を照会する）。
        g.MapGet("/state", (AppSvc svc) => Results.Ok(svc.GetLlmState()));

        // 現在月の費用÷資金比率（月報の費用レビュー）。
        g.MapGet("/review", (AppSvc svc, decimal capital) => Results.Ok(new { ratio = svc.Review(capital) }));

        return app;
    }
}

// 費用計上の要求（カテゴリ・金額[円]）。
internal sealed record RecordCostRequest(CostCategory Category, decimal Amount);
