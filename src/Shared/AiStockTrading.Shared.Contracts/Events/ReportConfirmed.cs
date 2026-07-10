namespace AiStockTrading.Shared.Contracts.Events;

// FR-07, FR-09, UC-03〜05, IADR-0024: 報告書が利用者により確定された（Draft→Confirmed の遷移時のみ発行）。
// 通知サービスが購読して Discord 通知する。KB 保存など後続の購読者もここに追加する。
public record ReportConfirmed(
    string PeriodKey,
    string Kind,
    int AssumptionsVersion,
    DateTimeOffset ConfirmedAt);
