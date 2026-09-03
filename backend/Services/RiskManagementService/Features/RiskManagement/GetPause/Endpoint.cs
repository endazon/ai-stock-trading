namespace RiskManagementService.Features.RiskManagement.GetPause;

// FR-10, FR-14, UC-06, ADR-0009: 取引の一時停止の現況（OwnerOnly）。
internal static class GetPauseEndpoint
{
    public static void MapGetPause(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/pause", (PauseService svc) => Results.Ok(svc.GetState()));
}
