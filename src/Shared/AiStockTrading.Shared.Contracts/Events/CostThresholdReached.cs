namespace AiStockTrading.Shared.Contracts.Events;

// NFR（費用）, FR-09, IADR-0027: 費用がしきい値に到達し統制状態が上方に遷移した（Normal→Throttled→Halted）。
// 通知サービスが購読して Discord 通知する（100% 停止等）。Month は "yyyy-MM"、State は "Throttled"/"Halted"。
public record CostThresholdReached(
    string Month,
    string Category,
    decimal Percent,
    string State,
    DateTimeOffset OccurredAt);
