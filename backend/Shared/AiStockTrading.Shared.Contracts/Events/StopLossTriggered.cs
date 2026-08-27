using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-03, FR-10, UC-02, ADR-0003, IADR-0014, #331, IADR-0210: 市場監視が保有銘柄の損切りライン到達を検知した。
// **検知・記録・通知のみの経路であり、受け手は決済注文を発行しない**（損切りの実行はブローカー側の
// 逆指値が担う。planning#88 裁定・FR-10。二重決済の防止）。監査（FR-11）・Discord 通知（FR-09）が購読する。
// PositionSide は建玉方向（Buy 建て=ロング / Sell 建て=ショート）。
// Quantity は建玉数量、StopLossPrice は到達した損切り価格、Price は検知時点の現在値。
public record StopLossTriggered(
    Guid EventId,
    string Symbol,
    Market Market,
    TradeSide PositionSide,
    int Quantity,
    decimal Price,
    decimal StopLossPrice,
    DateTimeOffset DetectedAt);
