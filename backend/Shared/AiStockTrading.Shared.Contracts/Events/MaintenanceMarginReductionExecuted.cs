using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, FR-11, UC-06, ADR-0016 決定7, #330, IADR-0133 決定7: 維持率割れによる建玉の自動縮小が発動した。
//
// **記録先 4 つ（監査ログ・Discord 通知・日報・月報）が写像する唯一の情報源**である。記録先ごとに別の情報源を
// 持つと、同じ発動が記録先ごとに違う値で残り、「規則どおりに作動したか」を人が事後に検証できるという
// 記録の目的（04_report-templates 日報 §4）が壊れる。
//
// 本イベントは 04_report-templates の日報の表が要求する 7 列をすべて持つ:
//   時刻 / 決済前の維持率 / 閾値 / 回復目標（閾値+5pt） / 決済した建玉（銘柄・方向・数量・必要証拠金） / 決済後の維持率
public record MaintenanceMarginReductionExecuted(
    Guid EventId,
    decimal RatioBefore,
    decimal Threshold,
    decimal RecoveryTarget,
    decimal? RatioAfter,
    IReadOnlyList<MaintenanceMarginReductionItem> Items,
    DateTimeOffset ExecutedAt);
