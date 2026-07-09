using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.MarketMonitor.Domain;

// FR-03, FR-10: 損切りライン検知のための保有ポジション。実データは #13/#17 連携で供給する。
// Side は建玉方向（Buy 建て=ロング / Sell 建て=ショート）。StopLossPrice は損切り価格（所与）。
public record HeldPosition(
    string Symbol,
    Market Market,
    TradeSide Side,
    int Quantity,
    decimal EntryPrice,
    decimal StopLossPrice);
