namespace AiStockTrading.Shared.Contracts.Events;

// UC-01, FR-09, FR-07, FR-11, IADR-0096, #210: 確定済み日報の方針が無いため取引サイクルがスキップされた。
// 取引判断（TradeDecisionService・#11）が policy-null で見送った営業日について、同一営業日内は 1 回だけ発行する
// （営業日単位の重複抑止は発行側で行う・IADR-0096）。NotificationService が購読して利用者へ「日報の確定を促す」通知を
// 送り（FR-09）、AuditService が中央監査台帳へ集約する（FR-11: 全イベントの時系列記録）。
// 日報方針はグローバル（銘柄非依存）のため銘柄・市場は持たせない。
public record DailyPolicyUnconfirmed(
    DateOnly BusinessDay,
    DateTimeOffset OccurredAt);
