namespace RiskManagementService.Features.RiskManagement.ResumeTrading;

// FR-10, FR-14, UC-06, ADR-0009: 取引の再開（OwnerOnly・理由必須）。
// `/resume` は pause のみ解除する（日次損失ロックアウトは解除しない）。操作は冪等
// （非停止中の resume は現状態を返すのみ）。actor は認証済みトークン名。
internal static class ResumeTradingEndpoint
{
    public static void MapResumeTrading(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/resume", (PauseRequest req, PauseService svc, HttpContext http) =>
            Results.Ok(svc.Resume(RiskControlEndpoints.ActorOf(http), req.Reason)));
}
