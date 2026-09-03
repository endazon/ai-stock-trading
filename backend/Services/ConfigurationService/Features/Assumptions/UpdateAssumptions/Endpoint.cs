using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Kernel.Trading;
using ConfigurationService.Common.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;

namespace ConfigurationService.Features.Assumptions.UpdateAssumptions;

// FR-17, UC-06: 前提条件の更新（利用者のみ・理由必須・楽観排他）。成功時に AssumptionsChanged を発行し通知サービスへ伝える。
internal static class UpdateAssumptionsEndpoint
{
    public static void MapUpdateAssumptions(this IEndpointRouteBuilder owner) =>
        owner.MapPut("", async (UpdateAssumptionsRequest req, AssumptionsService svc, IMessageBus bus, IClock clock, HttpContext http) =>
        {
            var actor = ActorOf(http);
            var version = svc.Update(req.Assumptions, req.ExpectedVersion, actor, req.Reason);
            await bus.PublishAsync(new AssumptionsChanged(version, actor, req.Reason, clock.UtcNow));
            return Results.Ok(svc.GetCurrent());
        });

    private static string ActorOf(HttpContext http) =>
        http.User.Identity?.Name is { Length: > 0 } name ? name : "unknown";
}

// 前提条件更新の要求。TradingAssumptions は具象レコードのため標準の逆直列化が可能。ExpectedVersion で楽観排他する。
internal sealed record UpdateAssumptionsRequest(TradingAssumptions Assumptions, int ExpectedVersion, string Reason);
