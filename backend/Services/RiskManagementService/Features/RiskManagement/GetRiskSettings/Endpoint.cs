namespace RiskManagementService.Features.RiskManagement.GetRiskSettings;

// ---- 設定（FR-10/FR-19/FR-20, ADR-0003/ADR-0007/ADR-0008） ----
internal static class GetRiskSettingsEndpoint
{
    public static void MapGetRiskSettings(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/settings", (RiskSettingsService svc) => Results.Ok(svc.GetCurrent()));
}
