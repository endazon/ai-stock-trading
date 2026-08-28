using AuditService.Application.Ports;
using AuditService.Application.State;
using Microsoft.EntityFrameworkCore;

namespace AuditService.Infrastructure.Persistence;

// FR-11, IADR-0019: 監査台帳の EF 実装（追記専用・専有 DB）。Id（=MessageId）で冪等。相関・期間で照会する。
internal sealed class EfAuditEventStore(AuditDbContext db) : IAuditEventStore
{
    public void Append(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // 冪等: 同一 Id（ブローカの再送）は無視する。
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

    // FR-06, FR-11, #381, IADR-0199 決定2: 期間の集計（日報の為替欄）が引く経路。
    //
    // 🔴 **上限を持たない。** `GetRecent(大きな limit)` で代用すると、期間内の件数が上限を超えたとき
    // **古いものから静かに落ちる**——取りこぼしても赤くならない。
    public IReadOnlyList<AuditEntry> GetByTypesInPeriod(
        IReadOnlyCollection<string> eventTypes,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);

        // 種別が空なら結果も空になる（`Contains` が `IN ()` へ落ち、1 件も一致しない）。
        // **明示的な早期 return は置かない**——振る舞いが変わらない防御は何も守らない（IADR-0199 の変異試験）。
        // 「種別の指定漏れ」を止めるのは**エンドポイント側の 400** である。
        //
        // 半開区間。終端を閉じるとその日の最後の 1 秒が落ちる。
        return [.. db.AuditEvents
            .Where(r => eventTypes.Contains(r.EventType))
            .Where(r => r.OccurredAt >= fromInclusive && r.OccurredAt < toExclusive)
            .OrderBy(r => r.OccurredAt)
            .Select(r => ToEntry(r))];
    }

    private static AuditEntry ToEntry(AuditEventRow r) => new(
        r.Id, r.EventType, r.CorrelationId, r.Symbol, r.Summary, r.Detail, r.OccurredAt, r.RecordedAt);
}
