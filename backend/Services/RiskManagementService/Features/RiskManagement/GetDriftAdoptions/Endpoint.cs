namespace RiskManagementService.Features.RiskManagement.GetDriftAdoptions;

// 期間の乖離の取り込み（FR-06, FR-11, FR-16, ADR-0041 決定 1, #870, #859, IADR-0360 決定 2）:
// 報告書サービス（#14）が日報 §2-b「手動売買（損益不明）」と在庫の畳み込みのため同期照会する。
// 取引台帳の取り込み行を取引日で絞って返す。読み取り専用で、新規テーブル・新規イベントは持たない。
// 期間が逆順でも 200（空列）＝報告書生成を止めない（GET /fills と同じ作法）。
//
// 🔴 **GET /fills とは別の口である。** 取り込みは約定価格を持たず、実現損益は**不明**である。
// 1 つの列に混ぜると、消費側が除外を書き落としたときに「損益 0 の決済」が確定値として集計される。
internal static class GetDriftAdoptionsEndpoint
{
    public static void MapGetDriftAdoptions(this IEndpointRouteBuilder read) =>
        read.MapGet("/drift-adoptions", (DateOnly? from, DateOnly? to, IPortfolioLedgerStore ledger) =>
        {
            if (from is not { } fromDay || to is not { } toDay)
                return Results.BadRequest(new { error = "from・to（yyyy-MM-dd）は必須です。" });

            return Results.Ok(
                PeriodDriftAdoptionQuery.InTradingDayRange(ledger.GetDriftAdoptions(), fromDay, toDay));
        });
}
