namespace RiskManagementService.Features.RiskManagement.DisengageKillSwitch;

// FR-10, FR-14, UC-06, ADR-0003: kill switch の解除（OwnerOnly・理由必須）。
internal static class DisengageKillSwitchEndpoint
{
    public static void MapDisengageKillSwitch(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/kill-switch/disengage", (KillSwitchRequest req, KillSwitchService svc, HttpContext http) =>
        {
            svc.Disengage(RiskControlEndpoints.ActorOf(http), req.Reason);
            return Results.Ok(svc.GetState());
        });
}
