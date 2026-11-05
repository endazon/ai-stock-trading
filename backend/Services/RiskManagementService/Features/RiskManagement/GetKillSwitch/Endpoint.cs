namespace RiskManagementService.Features.RiskManagement.GetKillSwitch;

// FR-10, UC-06, ADR-0003: kill switch の現況（OwnerOnly）。
internal static class GetKillSwitchEndpoint
{
    public static void MapGetKillSwitch(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/kill-switch", (KillSwitchService svc, HttpContext http) =>
        {
            // 🔴 FR-10, #1534, IADR-0620: 見た状態の版を本文（version）と ETag の両方で返す（発動・解除の前提条件に使う）。
            var state = svc.GetState();
            http.Response.Headers.ETag = $"\"{state.Version}\"";
            return Results.Ok(state);
        });
}
