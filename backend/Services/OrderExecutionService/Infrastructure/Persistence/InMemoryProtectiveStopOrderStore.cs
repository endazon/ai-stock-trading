using System.Collections.Concurrent;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Domain;

namespace OrderExecutionService.Infrastructure.Persistence;

// FR-10, #331, IADR-0210: 保護逆指値レグ記録のインメモリ実装（paper 構成・単体テスト用）。
public sealed class InMemoryProtectiveStopOrderStore : IProtectiveStopOrderStore
{
    private readonly ConcurrentDictionary<Guid, ProtectiveStopOrder> _stops = new();

    public void Save(ProtectiveStopOrder stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        _stops[stop.EntryDecisionId] = stop;
    }

    public ProtectiveStopOrder? Find(Guid entryDecisionId) =>
        _stops.TryGetValue(entryDecisionId, out var stop) ? stop : null;

    public IReadOnlyList<ProtectiveStopOrder> FindActive(int batchSize) =>
        _stops.Values
            .Where(s => s.State == ProtectiveStopState.Active)
            .OrderBy(s => s.CreatedAt)
            .Take(batchSize)
            .ToList();
}
