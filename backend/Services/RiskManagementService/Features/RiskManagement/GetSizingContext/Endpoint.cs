namespace RiskManagementService.Features.RiskManagement.GetSizingContext;

// サイジング文脈（FR-04/10, IADR-0029）: 取引判断（#11）が同期照会する（設定＋ポートフォリオ状態から導出）。
internal static class GetSizingContextEndpoint
{
    public static void MapGetSizingContext(this IEndpointRouteBuilder read) =>
        read.MapGet("/sizing-context", (SizingContextService svc) => Results.Ok(svc.Build()));
}
