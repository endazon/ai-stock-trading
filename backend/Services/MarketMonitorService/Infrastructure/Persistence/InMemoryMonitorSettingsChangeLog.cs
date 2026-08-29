using MarketMonitorService.Features.MarketMonitor;

namespace MarketMonitorService.Infrastructure.Persistence;

// FR-11, FR-13: 監視設定変更履歴のインメモリ実装。実運用の PostgreSQL 監査ログは EF 実装で差し替える。
public sealed class InMemoryMonitorSettingsChangeLog : IMonitorSettingsChangeLog
{
    private readonly Lock _gate = new();
    private readonly List<MonitorSettingsChangeEntry> _entries = [];

    public void Record(MonitorSettingsChangeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    public IReadOnlyList<MonitorSettingsChangeEntry> GetHistory()
    {
        lock (_gate)
        {
            // 新しい順で返す（照会 UC-06 の既定並び）。
            return _entries.AsEnumerable().Reverse().ToList();
        }
    }
}
