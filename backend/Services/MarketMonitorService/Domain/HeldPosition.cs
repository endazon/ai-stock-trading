using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Domain;

// FR-03, FR-10: 損切りライン検知のための保有ポジション。実データはリスク管理（#12/#63 台帳）の open-positions 照会（HttpPositionStore・IADR-0030）から供給される。
// Side は建玉方向（Buy 建て=ロング / Sell 建て=ショート）。StopLossPrice は損切り価格（所与）。
// #936, IADR-0393: 同じ銘柄に複数のエントリーがあれば、StopLossPrice は保有中のエントリーのうち最も保護的なライン
// （ロング: 最も高い）である。ここで到達を 1 件出し、S1 の行ごとの判定は発注執行が行う（IADR-0344 決定4）。
// #957, IADR-0399: EntryPrice は応答に無ければ null（不明）であり 0 ではない（市場監視は判定に使わない）。
public record HeldPosition(
    string Symbol,
    Market Market,
    TradeSide Side,
    int Quantity,
    decimal? EntryPrice,
    decimal StopLossPrice)
{
    /// <summary>
    /// 🔴 #957, IADR-0399 決定2: <see cref="StopLossPrice"/> が応答に無く（または正でなく）、平均取得単価から既定比率で
    /// <b>見積もった近似のライン</b>であるか。実値のラインと混ぜて表示しないために持つ（生存要約・閉場の報告が「近似」と書く）。
    /// </summary>
    public bool StopLossApproximated { get; init; }
}
