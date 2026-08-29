using AiStockTrading.Shared.Contracts.Events;

namespace MarketMonitorService.Features.MarketMonitor;

// FR-03: 1 巡回の判定結果。発行すべきイベント群を保持する。実際の発行（メッセージング）は Worker（Slice B）が行う。
public record MonitorRoundResult(
    IReadOnlyList<StopLossTriggered> StopLosses,
    IReadOnlyList<PriceMovementDetected> PriceMovements);
