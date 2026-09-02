namespace RiskManagementService.Features.RiskManagement.GetKillSwitch;

// FR-10, UC-06, ADR-0003: kill switch の現況（OwnerOnly）。
internal static class GetKillSwitchEndpoint
{
    public static void MapGetKillSwitch(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/kill-switch", (KillSwitchService svc) => Results.Ok(svc.GetState()));
}
