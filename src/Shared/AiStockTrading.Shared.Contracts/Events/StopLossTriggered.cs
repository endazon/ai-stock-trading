using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-03, FR-10, UC-02, ADR-0003, IADR-0014: 市場監視が保有銘柄の損切りライン到達を検知した。
// リスク管理サービス（#12 Slice C）が購読し、LLM を迂回して決済（Close）注文を機械的に発行する。
// PositionSide は建玉方向（Buy 建て=ロング / Sell 建て=ショート）。決済はこの反対売買になる。
// Quantity は決済すべき数量、StopLossPrice は到達した損切り価格、Price は検知時点の現在値。
public record StopLossTriggered(
    Guid EventId,
    string Symbol,
    Market Market,
    TradeSide PositionSide,
    int Quantity,
    decimal Price,
    decimal StopLossPrice,
    DateTimeOffset DetectedAt);
