using System.Collections.Concurrent;
using AuditService.Features.AuditEvents;

namespace AuditService.Infrastructure.Persistence;

// FR-11, IADR-0019: 監査台帳のインメモリ実装（テスト・単体実行用）。PostgreSQL 永続化は Worker の EfAuditEventStore で差し替える。
// Id（=MessageId）で冪等。
public sealed class InMemoryAuditEventStore : IAuditEventStore
{
    private readonly ConcurrentDictionary<Guid, AuditEntry> _entries = new();

    public void Append(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries.TryAdd(entry.Id, entry);
    }

    public IReadOnlyList<AuditEntry> GetByCorrelation(Guid correlationId) =>
        [.. _entries.Values.Where(e => e.CorrelationId == correlationId).OrderBy(e => e.OccurredAt)];

    public IReadOnlyList<AuditEntry> GetRecent(int limit) =>
        [.. _entries.Values.OrderByDescending(e => e.OccurredAt).Take(limit)];

    // #381, IADR-0199 決定2: 種別 × 期間（半開区間）。上限は持たない（取りこぼしが静かに起きる形にしない）。
    public IReadOnlyList<AuditEntry> GetByTypesInPeriod(
        IReadOnlyCollection<string> eventTypes,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);

        // 種別が空なら結果も空になる（下の絞り込みがそのまま効く）。**明示的な早期 return は置かない**
        // ——**振る舞いが 1 ミリも変わらない防御は、守っているように見えて何も守らない**
        // （変異試験で実測。`if (false)` にしてもテストが 1 本も落ちなかった）。
        // 「種別の指定漏れ」を止めるのは**エンドポイント側の 400** であり、そちらは実際に検査している。
        var wanted = eventTypes.ToHashSet(StringComparer.Ordinal);

        return [.. _entries.Values
            .Where(e => wanted.Contains(e.EventType))
            .Where(e => e.OccurredAt >= fromInclusive && e.OccurredAt < toExclusive)
            .OrderBy(e => e.OccurredAt)];
    }
}
