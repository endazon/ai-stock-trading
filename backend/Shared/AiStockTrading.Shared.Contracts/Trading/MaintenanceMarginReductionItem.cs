namespace AiStockTrading.Shared.Contracts.Trading;

// FR-10, UC-06, #330, IADR-0133: 自動縮小で決済した建玉 1 件（部分決済を含む）。
// `MaintenanceMarginReductionExecuted` の明細であり、日報の「決済した建玉（銘柄・方向・数量・必要証拠金）」に対応する。
//
// **Events 名前空間には置かない。** 同名前空間の record は EventTypeDiscovery が「ドメインイベント」として
// 数え上げ、監査購読・識別子固定・後方互換の対象になる（明細型はイベントではない）。
// Side は**建玉の**方向（決済注文の売買方向ではない）。
public record MaintenanceMarginReductionItem(
    string Symbol,
    Market Market,
    TradeSide PositionSide,
    ProductType ProductType,
    int Quantity,
    decimal PriceUsd,
    decimal RequiredMarginUsd);
