using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;

namespace RiskManagementService.Features.RiskManagement.ResumeTrading;

// FR-10, FR-14, UC-06, ADR-0009: 取引の再開（OwnerOnly・理由必須）。
// `/resume` は pause のみ解除する（日次損失ロックアウトは解除しない）。操作は冪等
// （非停止中の resume は現状態を返すのみ）。actor は認証済みトークン名。
internal static class ResumeTradingEndpoint
{
    public static void MapResumeTrading(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/resume", (PauseRequest req, PauseService svc, HttpContext http) => Handle(req, svc, http))
            // NFR-06, #1554, IADR-0622: ボットの許可表の経路（ボットの REST アダプタが呼ぶ）。
            .AllowOwnerBot();

    // NFR, IADR-0450, #753（段 5）: REST と gRPC 面（RiskControlsOwnerWriteGrpcService）が共有する処理（2 箇所に書かない）。
    internal static IResult Handle(PauseRequest req, PauseService svc, HttpContext http)
    {
        // NFR-06, FR-14, #1542, IADR-0615: 再開は危険な操作（第 2 要素）。
        return Results.Ok(svc.Resume(RiskControlEndpoints.ActorOf(http), req.Reason,
            RiskSecondFactor.BeforeCommit(http, AiStockTrading.Shared.Infrastructure.Composable.SecondFactor.SecondFactorOperation.TradingResume, AiStockTrading.Shared.Infrastructure.Composable.SecondFactor.OwnerSecondFactorTargets.TradingPause, req.SecondFactorCode)));
    }
}
