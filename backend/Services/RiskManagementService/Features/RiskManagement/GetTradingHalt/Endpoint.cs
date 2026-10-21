namespace RiskManagementService.Features.RiskManagement.GetTradingHalt;

// 🔴 FR-10, ADR-0009, IADR-0565 決定 2: kill switch・一時停止の成立の有無（OwnerOrService。発注執行が新規建てを送る直前に読む）。
// 操作（engage / pause 等）と操作者つきの現況（/kill-switch・/pause）は従来どおり OwnerOnly のまま。
internal static class GetTradingHaltEndpoint
{
    public static void MapGetTradingHalt(this IEndpointRouteBuilder read) =>
        read.MapGet("/trading-halt", (TradingHaltService svc) => Results.Ok(svc.Build()));
}
