using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.MarketMonitor.Domain;

// FR-03, FR-10: 損切りライン検知のための保有ポジション。実データはリスク管理（#12/#63 台帳）の open-positions 照会（HttpPositionStore・IADR-0030）から供給される。
// Side は建玉方向（Buy 建て=ロング / Sell 建て=ショート）。StopLossPrice は損切り価格（所与）。
public record HeldPosition(
    string Symbol,
    Market Market,
    TradeSide Side,
    int Quantity,
    decimal EntryPrice,
    decimal StopLossPrice);
