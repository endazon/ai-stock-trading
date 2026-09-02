using AiStockTrading.Shared.Contracts.Events;
using CostControlService.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Wolverine;
using AppSvc = CostControlService.Features.CostControl.CostControlAppService;

namespace CostControlService.Features.CostControl.RecordCost;

// NFR（費用）, FR-09, IADR-0027: 費用を計上する。統制状態が上方に遷移したら CostThresholdReached を発行する。
internal static class RecordCostEndpoint
{
    public static void MapRecordCost(this IEndpointRouteBuilder owner) =>
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
}

// 費用計上の要求（カテゴリ・金額[円]）。
internal sealed record RecordCostRequest(CostCategory Category, decimal Amount);
