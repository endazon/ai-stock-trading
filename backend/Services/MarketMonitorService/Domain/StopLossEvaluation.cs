using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Domain;

// FR-03, FR-10, ADR-0040 決定1（S1）, #902, IADR-0365 決定1: 1 巡回で保有ポジション 1 件の損切りラインを評価した記録。
// 価格が取れなかった保有も Price=null で残す（生存要約と価格欠落の Warning の材料。到達の判定には使わない）。
// #1280, IADR-0520: 到達の再発行の抑止（StopLossArrivalGate.Settle）も読む —— 価格を取ってラインの内側だった評価は
// 「戻った」証拠として記憶を解き、Price=null の評価は「分からない」として記憶を残す（到達を新しく作ることはない）。
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
