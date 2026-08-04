namespace AiStockTrading.Audit.Application.State;

// FR-11, UC-07, IADR-0019: 監査台帳の 1 記録（全ドメインイベントを共通形へ正規化したもの）。追記専用。
// Id は冪等キー（メッセージ ID＝Wolverine の Envelope.Id）。CorrelationId は注文系の DecisionId／市場系の EventId（注文の全体像を辿る）。
// Detail はイベント全量の JSON（列挙は文字列化）。Summary は人間可読の一行。OccurredAt はイベント時刻、RecordedAt は記録時刻。
public sealed record AuditEntry(
    Guid Id,
    string EventType,
    Guid CorrelationId,
    string? Symbol,
    string Summary,
    string Detail,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt);
