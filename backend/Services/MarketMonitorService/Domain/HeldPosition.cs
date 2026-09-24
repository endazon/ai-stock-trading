using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Domain;

// FR-03, FR-10: 損切りライン検知のための保有ポジション。実データはリスク管理（#12/#63 台帳）の open-positions 照会（HttpPositionStore・IADR-0030）から供給される。
// Side は建玉方向（Buy 建て=ロング / Sell 建て=ショート）。StopLossPrice は損切り価格（所与）。
// #936, IADR-0393: 同じ銘柄に複数のエントリーがあれば、StopLossPrice は保有中のエントリーのうち最も保護的なライン
// （ロング: 最も高い）である。ここで到達を 1 件出し、S1 の行ごとの判定は発注執行が行う（IADR-0344 決定4）。
public record HeldPosition(
    string Symbol,
    Market Market,
    TradeSide Side,
    int Quantity,
    decimal EntryPrice,
    decimal StopLossPrice);
