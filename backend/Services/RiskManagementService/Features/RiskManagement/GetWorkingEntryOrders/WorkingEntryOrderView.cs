using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement.GetWorkingEntryOrders;

// FR-04, FR-10, #934, IADR-0390 決定1: 未約定の新規建て注文 1 件（判断の入力）。
// RemainingQuantity は**残数量**（承認数量 − 同じ DecisionId の約定累計）。約定済みの分は /open-positions 側の
// 建玉に入っており、ここには入らない（二重に数えない）。Price は承認価格（ローカル通貨）。
//
// 🔴 「ブローカーが受理した」ことは表さない。承認済みで終端イベントが届いていない注文である（受理済み・発注処理中・
// 結果未着の区別を注文アクティビティは持たない。IADR-0390「原則 A の扱い」）。
public sealed record WorkingEntryOrderView(
    Guid DecisionId,
    string Symbol,
    Market Market,
    TradeSide Side,
    int RemainingQuantity,
    decimal Price,
    DateTimeOffset ApprovedAt);
