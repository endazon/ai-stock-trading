using AiStockTrading.Shared.Contracts.Events;

namespace AiStockTrading.MarketMonitor.Application.Services;

// FR-03: 1 巡回の判定結果。発行すべきイベント群を保持する。実際の発行（MassTransit）は Worker（Slice B）が行う。
public record MonitorRoundResult(
    IReadOnlyList<StopLossTriggered> StopLosses,
    IReadOnlyList<PriceMovementDetected> PriceMovements);
