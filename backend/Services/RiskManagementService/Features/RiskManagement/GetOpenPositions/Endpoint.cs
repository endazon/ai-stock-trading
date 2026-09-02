namespace RiskManagementService.Features.RiskManagement.GetOpenPositions;

// 保有ポジション（FR-03/10, IADR-0030）: 市場監視（#10）が損切りライン検知のため同期照会する
// （#63 台帳の射影＋損切り価格の近似導出）。
internal static class GetOpenPositionsEndpoint
{
    public static void MapGetOpenPositions(this IEndpointRouteBuilder read) =>
        read.MapGet("/open-positions", (OpenPositionsService svc) => Results.Ok(svc.Build()));
}
