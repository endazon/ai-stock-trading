namespace AuditService.Infrastructure.Persistence;

// FR-11, IADR-0019: 監査記録の行モデル（追記専用）。ADR-0001 の専有 DB（audit_svc）に配置する。
// Id はメッセージ ID（Wolverine の Envelope.Id。冪等キー・PK）。Detail はイベント全量 JSON（jsonb）。
// NFR/IADR-0259 決定4: AuditDbContext.AuditEvents（public DbSet）が要求するため public にする。
public sealed class AuditEventRow
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
