using AiStockTrading.Audit.Application.Ports;
using AiStockTrading.Audit.Application.State;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.Audit.Infrastructure.Foundation.Persistence;

// FR-11, IADR-0019: 監査台帳の EF 実装（追記専用・専有 DB）。Id（=MessageId）で冪等。相関・期間で照会する。
internal sealed class EfAuditEventStore(AuditDbContext db) : IAuditEventStore
{
    public void Append(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // 冪等: 同一 Id（MassTransit 再送）は無視する。
        if (db.AuditEvents.Find(entry.Id) is not null)
            return;

        db.AuditEvents.Add(new AuditEventRow
        {
            Id = entry.Id,
            EventType = entry.EventType,
            CorrelationId = entry.CorrelationId,
            Symbol = entry.Symbol,
            Summary = entry.Summary,
            Detail = entry.Detail,
            OccurredAt = entry.OccurredAt,
            RecordedAt = entry.RecordedAt,
        });
        db.SaveChanges();
    }

    public IReadOnlyList<AuditEntry> GetByCorrelation(Guid correlationId) =>
        [.. db.AuditEvents
            .Where(r => r.CorrelationId == correlationId)
            .OrderBy(r => r.OccurredAt)
            .Select(r => ToEntry(r))];

    public IReadOnlyList<AuditEntry> GetRecent(int limit) =>
        [.. db.AuditEvents
            .OrderByDescending(r => r.OccurredAt)
            .Take(limit)
            .Select(r => ToEntry(r))];

    private static AuditEntry ToEntry(AuditEventRow r) => new(
        r.Id, r.EventType, r.CorrelationId, r.Symbol, r.Summary, r.Detail, r.OccurredAt, r.RecordedAt);
}
