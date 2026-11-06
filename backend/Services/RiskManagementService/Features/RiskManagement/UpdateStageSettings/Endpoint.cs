using RiskManagementService.Domain;

namespace RiskManagementService.Features.RiskManagement.UpdateStageSettings;

// FR-20, ADR-0008: 運用段階の設定変更（OwnerOnly）。
// 🔴 #1533, IADR-0616 決定1: 段階の変更は段階ゲートの遷移（POST /risk-controls/stage-gate/transition。合格・撤退基準と利用者の承認）だけで行う
// （FR-20「段階遷移は合格・撤退基準に基づき利用者の承認で行う」）。この口は段階を変えず 409 を返す（呼び手へ正しい口を示すために残す）。
internal static class UpdateStageSettingsEndpoint
{
    public const string Message =
        "運用段階は段階ゲートの遷移（POST /risk-controls/stage-gate/transition）でだけ変えられます。この口では変更しません。";

    public static void MapUpdateStageSettings(this IEndpointRouteBuilder owner) =>
        owner.MapPut("/settings/stage", (StageUpdateRequest req) =>
            Results.Conflict(new { error = Message }));
}

// 段階変更の要求（受けるが適用しない。#1533）。
internal sealed record StageUpdateRequest(StageSettings Stage, string Reason);
