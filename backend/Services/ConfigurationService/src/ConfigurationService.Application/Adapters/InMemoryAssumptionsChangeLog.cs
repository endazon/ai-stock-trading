using ConfigurationService.Application.Ports;
using ConfigurationService.Application.State;

namespace ConfigurationService.Application.Adapters;

// FR-17: 前提条件変更履歴のインメモリ実装（テスト・単体実行用）。追記専用・新しい順で返す。
public sealed class InMemoryAssumptionsChangeLog : IAssumptionsChangeLog
{
    private readonly Lock _gate = new();
    private readonly List<AssumptionsChangeEntry> _entries = [];

    public void Record(AssumptionsChangeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    public IReadOnlyList<AssumptionsChangeEntry> GetHistory()
    {
        lock (_gate)
        {
            return [.. _entries.OrderByDescending(e => e.ChangedAt)];
        }
    }
}
