using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Domain;

// FR-03, FR-10, ADR-0040 決定1（S1）, #902, IADR-0365 決定1: 1 巡回で保有ポジション 1 件の損切りラインを評価した記録。
// 価格が取れなかった保有も Price=null で残す（生存要約と価格欠落の Warning の材料。判定・発行には使わない）。
public sealed record StopLossEvaluation(
    string Symbol,
    Market Market,
    TradeSide Side,
    int Quantity,
    decimal StopLossPrice,
    decimal? Price,
    DateTimeOffset EvaluatedAt)
{
    /// <summary>#957, IADR-0399 決定2: <see cref="StopLossPrice"/> が近似のラインか（<see cref="HeldPosition.StopLossApproximated"/> の写し）。</summary>
    public bool StopLossApproximated { get; init; }
}
