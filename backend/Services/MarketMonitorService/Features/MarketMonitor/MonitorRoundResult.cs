using AiStockTrading.Shared.Contracts.Events;
using MarketMonitorService.Domain;

namespace MarketMonitorService.Features.MarketMonitor;

// FR-03: 1 巡回の判定結果。発行すべきイベント群を保持する。実際の発行（メッセージング）は Worker（Slice B）が行う。
public record MonitorRoundResult(
    IReadOnlyList<StopLossTriggered> StopLosses,
    IReadOnlyList<PriceMovementDetected> PriceMovements)
{
    /// <summary>
    /// FR-10, #902, IADR-0365 決定1: この巡回で評価した保有ポジションごとの記録（価格が取れなかった保有も含む）。
    /// 生存要約（StopLossLivenessReporter）だけが読む。到達の判定・発行には使わない。
    /// </summary>
    public IReadOnlyList<StopLossEvaluation> StopLossEvaluations { get; init; } = [];

    /// <summary>
    /// FR-03, FR-10, #909, IADR-0380 決定2・決定3: この巡回で**市場が閉場していたため評価しなかった**保有ポジション。
    /// <see cref="StopLossEvaluation.Price"/> は常に <c>null</c> である（価格照会そのものを行っていない。
    /// 「照会したが取れなかった」＝<see cref="StopLossEvaluations"/> 側の欠落とは別の事実）。
    /// 保護の空白を声に出すため（StopLossLivenessReporter）だけに使う。到達の判定・発行には使わない。
    /// </summary>
    public IReadOnlyList<StopLossEvaluation> ClosedMarketPositions { get; init; } = [];
}
