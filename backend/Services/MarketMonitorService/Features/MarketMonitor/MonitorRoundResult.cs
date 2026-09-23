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
}
