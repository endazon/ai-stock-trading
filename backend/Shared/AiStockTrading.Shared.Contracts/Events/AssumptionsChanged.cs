namespace AiStockTrading.Shared.Contracts.Events;

// FR-17, UC-06, IADR-0021: 全体前提条件（TradingAssumptions）が利用者により変更された（バージョンつき）。
// 通知サービスが購読して Discord 通知する（各サービスは Discord を直接呼ばない・IADR-0020）。
public record AssumptionsChanged(
    int Version,
    string Actor,
    string Reason,
    DateTimeOffset ChangedAt);
