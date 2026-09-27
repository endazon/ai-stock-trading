namespace RiskManagementService.Features.RiskManagement.DisengageKillSwitch;

// FR-10, FR-14, UC-06, ADR-0003: kill switch の解除（OwnerOnly・理由必須）。
internal static class DisengageKillSwitchEndpoint
{
    public static void MapDisengageKillSwitch(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/kill-switch/disengage", (KillSwitchRequest req, KillSwitchService svc, HttpContext http) => Handle(req, svc, http));

    // NFR, IADR-0450, #753（段 5）: REST と gRPC 面（RiskControlsOwnerWriteGrpcService）が共有する処理（2 箇所に書かない）。
    internal static IResult Handle(KillSwitchRequest req, KillSwitchService svc, HttpContext http)
    {
        svc.Disengage(RiskControlEndpoints.ActorOf(http), req.Reason);
        return Results.Ok(svc.GetState());
    }
}
