namespace AiStockTrading.Audit.Infrastructure.Foundation.Persistence;

// FR-11, IADR-0019: 監査記録の行モデル（追記専用）。ADR-0001 の専有 DB（audit_svc）に配置する。
// Id は MassTransit MessageId（冪等キー・PK）。Detail はイベント全量 JSON（jsonb）。
internal sealed class AuditEventRow
{
    public Guid Id { get; set; }

    public string EventType { get; set; } = string.Empty;

    public Guid CorrelationId { get; set; }

    public string? Symbol { get; set; }

    public string Summary { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
}
