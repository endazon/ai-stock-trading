namespace RiskManagementService.Features.RiskManagement.GetWorkingEntryOrders;

// FR-04, FR-10, ADR-0003, #934, IADR-0390 決定1: 当日の未約定の新規建て注文（承認済み・未終端・残数量 > 0）。
// 取引判断が判断の入力（約定済みの保有とは**別の第 3 の状態**）として同期照会する。定義は統制（IADR-0346）と同じ
// PortfolioProjection.ProjectWorkingEntries である。読み取り専用で、新規テーブル・新規イベントは持たない。
internal static class GetWorkingEntryOrdersEndpoint
{
    public static void MapGetWorkingEntryOrders(this IEndpointRouteBuilder read) =>
        read.MapGet("/working-entry-orders", (WorkingEntryOrdersService svc) => Results.Ok(svc.Build()));
}
