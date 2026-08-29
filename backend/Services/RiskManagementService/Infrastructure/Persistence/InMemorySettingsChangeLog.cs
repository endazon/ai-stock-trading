using RiskManagementService.Features.RiskManagement;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-11: 変更履歴のインメモリ実装（dev/test 用）。実運用は PostgreSQL 実装（EfSettingsChangeLog）が担う。
public sealed class InMemorySettingsChangeLog : ISettingsChangeLog
{
    private readonly Lock _gate = new();
    private readonly List<SettingsChangeEntry> _entries = [];

    public void Record(SettingsChangeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    public IReadOnlyList<SettingsChangeEntry> GetHistory()
    {
        lock (_gate)
        {
            // 新しい順で返す（照会 UC-06/UC-07 の既定並び）。
            return _entries.AsEnumerable().Reverse().ToList();
        }
    }
}
