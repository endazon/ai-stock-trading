namespace RiskManagementService.Features.RiskManagement.EngageKillSwitch;

// FR-10, FR-14, UC-06, ADR-0003: kill switch の起動（OwnerOnly・理由必須）。
internal static class EngageKillSwitchEndpoint
{
    public static void MapEngageKillSwitch(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/kill-switch/engage", (KillSwitchRequest req, KillSwitchService svc, HttpContext http) =>
        {
            svc.Engage(RiskControlEndpoints.ActorOf(http), req.Reason);
            return Results.Ok(svc.GetState());
        });
}
