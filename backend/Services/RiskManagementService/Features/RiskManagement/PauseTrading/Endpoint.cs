namespace RiskManagementService.Features.RiskManagement.PauseTrading;

// FR-10, FR-14, UC-06, ADR-0009: 取引の一時停止（OwnerOnly・理由必須）。
// pause は kill switch より軽い統制である。操作は冪等（停止中の再 pause は現状態を返すのみ）。
internal static class PauseTradingEndpoint
{
    public static void MapPauseTrading(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/pause", (PauseRequest req, PauseService svc, HttpContext http) => Handle(req, svc, http));

    // NFR, IADR-0450, #753（段 5）: REST と gRPC 面（RiskControlsOwnerWriteGrpcService）が共有する処理（2 箇所に書かない）。
    internal static IResult Handle(PauseRequest req, PauseService svc, HttpContext http) =>
        Results.Ok(svc.Pause(RiskControlEndpoints.ActorOf(http), req.Reason));
}
