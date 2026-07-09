using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;

namespace AiStockTrading.RiskManagement.Application.Adapters;

// FR-11, ADR-0007: 変更履歴のインメモリ実装。PostgreSQL 監査ログ（#17）は Slice B/後続で差し替える。
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
