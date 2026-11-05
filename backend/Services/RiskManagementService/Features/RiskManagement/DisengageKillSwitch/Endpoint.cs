using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;

namespace RiskManagementService.Features.RiskManagement.DisengageKillSwitch;

// FR-10, FR-14, UC-06, ADR-0003: kill switch の解除（OwnerOnly・理由必須）。
internal static class DisengageKillSwitchEndpoint
{
    public static void MapDisengageKillSwitch(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/kill-switch/disengage", (KillSwitchRequest req, KillSwitchService svc, HttpContext http) => Handle(req, svc, http))
            // NFR-06, #1554, IADR-0622: ボットの許可表の経路（ボットの REST アダプタが呼ぶ）。
            .AllowOwnerBot();

    // NFR, IADR-0450, #753（段 5）: REST と gRPC 面（RiskControlsOwnerWriteGrpcService）が共有する処理（2 箇所に書かない）。
    internal static IResult Handle(KillSwitchRequest req, KillSwitchService svc, HttpContext http)
    {
        // NFR-06, FR-14, #1542, IADR-0615: 解除は危険な操作（第 2 要素。門は状態を変える直前にサービスが呼ぶ）。
        // #1534, IADR-0620: 見た状態の版（任意）。本文の expectedVersion を優先し、無ければ If-Match を使う。
        svc.Disengage(RiskControlEndpoints.ActorOf(http), req.Reason,
            RiskSecondFactor.BeforeCommit(http, AiStockTrading.Shared.Infrastructure.Composable.SecondFactor.SecondFactorOperation.KillSwitchDisengage, req.SecondFactorCode),
            KillSwitchPrecondition.ExpectedVersionOf(req, http));
        return Results.Ok(svc.GetState());
    }
}
