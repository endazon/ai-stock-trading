namespace RiskManagementService.Features.RiskManagement.GetFills;

// 期間の約定（FR-06/16, IADR-0115 決定5, #280）: 報告書サービス（#14）が日報/週報/月報の数値集計のため
// 同期照会する。取引台帳（承認 Intent × 約定）を取引日（JST 境界）で絞って返す。読み取り専用で、
// 新規テーブル・新規イベントは持たない。期間が逆順・未指定でも 200（空列）＝報告書生成を止めない。
internal static class GetFillsEndpoint
{
    public static void MapGetFills(this IEndpointRouteBuilder read) =>
        read.MapGet("/fills", (DateOnly? from, DateOnly? to, IPortfolioLedgerStore ledger) =>
        {
            if (from is not { } fromDay || to is not { } toDay)
                return Results.BadRequest(new { error = "from・to（yyyy-MM-dd）は必須です。" });

            return Results.Ok(PeriodFillQuery.InTradingDayRange(ledger.GetFills(), fromDay, toDay));
        });
}
