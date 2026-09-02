using RiskManagementService.Domain;

namespace RiskManagementService.Features.RiskManagement.UpdateStageSettings;

// FR-20, ADR-0008: 運用段階の設定変更（OwnerOnly・理由必須）。
internal static class UpdateStageSettingsEndpoint
{
    public static void MapUpdateStageSettings(this IEndpointRouteBuilder owner) =>
        owner.MapPut("/settings/stage", (StageUpdateRequest req, RiskSettingsService svc, HttpContext http) =>
        {
            svc.UpdateStage(req.Stage, RiskControlEndpoints.ActorOf(http), req.Reason);
            return Results.Ok(svc.GetCurrent());
        });
}

// 段階変更の要求。
internal sealed record StageUpdateRequest(StageSettings Stage, string Reason);
