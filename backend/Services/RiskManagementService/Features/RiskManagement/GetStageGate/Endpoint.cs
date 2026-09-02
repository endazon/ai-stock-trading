namespace RiskManagementService.Features.RiskManagement.GetStageGate;

// ---- 段階ゲート（FR-20, UC-06, ADR-0008, IADR-0041/0070）----
// FR-06, FR-20, #569, IADR-0051, IADR-0271: **読み取り系（OwnerOrService）へ移した。**
// 報告書サービスが月報 §5 の三者比較で「その段に到達しているか」を知る必要があり、
// 到達していない段（空欄）と到達済みで 0 件の段を区別できないと、計画が求める書き分けができない。
// 読み取り専用であり、遷移（POST /stage-gate/transition）は OwnerOnly のまま据え置く。
// 機微情報を含む /status（OwnerOnly）とは別物である——本応答は段階と閾値・進捗のみを持つ。
internal static class GetStageGateEndpoint
{
    public static void MapGetStageGate(this IEndpointRouteBuilder read) =>
        read.MapGet("/stage-gate", (StageGateService svc) => Results.Ok(svc.GetStatus()));
}
