namespace RiskManagementService.Features.RiskManagement.GetStageGateHistory;

// FR-20, FR-11, UC-06, ADR-0008: 段階遷移の承認履歴（OwnerOnly）。
internal static class GetStageGateHistoryEndpoint
{
    public static void MapGetStageGateHistory(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/stage-gate/history", (StageGateService svc) => Results.Ok(svc.GetHistory()));
}
