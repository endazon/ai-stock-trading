namespace RiskManagementService.Features.RiskManagement.EvaluateWithdrawal;

// 撤退基準の評価＋自動安全側（HaltNewEntries なら kill switch を自動起動）。実降格は行わず降格提案を返す。
// 応答は撤退判定（Assessment）のみ（NewlyEngaged はドライバ #166 用の内部フラグ・API 契約は不変）。
internal static class EvaluateWithdrawalEndpoint
{
    public static void MapEvaluateWithdrawal(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/stage-gate/withdrawal/evaluate", (StageGateService svc) => Handle(svc));

    // NFR, IADR-0450, #753（段 5）: REST と gRPC 面（RiskControlsOwnerWriteGrpcService）が共有する処理（2 箇所に書かない）。
    internal static IResult Handle(StageGateService svc) => Results.Ok(svc.EvaluateWithdrawal().Assessment);
}
