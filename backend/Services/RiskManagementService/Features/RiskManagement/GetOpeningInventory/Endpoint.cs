using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement.GetOpeningInventory;

// FR-06, FR-16, #1181, IADR-0493 決定 2: 期間開始時点の在庫（報告書サービスが同期照会する。OwnerOrService）。
// market（数値または名前）・before（yyyy-MM-dd。**この現地取引日より前**＝排他）は必須。欠落・未定義の市場は 400
// （GET /entry-blockers・GET /fills と同じ向き）。読み取り専用で、新規テーブル・新規イベントは持たない。
//
// 🔴 **GET /open-positions とは別の口である。** あちらは**現在**の台帳全体を畳み損切りラインを載せる（市場監視・取引判断の入力）。
// こちらは**過去の取引日の境界まで**を畳み、基準通貨の平均取得単価と認識時レートを返す（報告書の在庫の初期値）。
internal static class GetOpeningInventoryEndpoint
{
    public static void MapGetOpeningInventory(this IEndpointRouteBuilder read) =>
        read.MapGet("/opening-inventory", (Market? market, DateOnly? before, IPortfolioLedgerStore ledger) =>
            market is not { } m || !Enum.IsDefined(m) || before is not { } beforeDay
                ? Results.BadRequest(new { error = "market・before（yyyy-MM-dd）は必須です。" })
                : Results.Ok(OpeningInventoryQuery.AsOf(ledger.GetFills(), m, beforeDay)));
}
