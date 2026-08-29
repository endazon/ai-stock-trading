using MarketMonitorService.Features.MarketMonitor;
using Microsoft.EntityFrameworkCore;

namespace MarketMonitorService.Infrastructure.Persistence;

// FR-11, FR-13: 監視設定変更履歴の EF 実装（追記専用）。新しい順で照会する。Risk の EfSettingsChangeLog をミラーする。
internal sealed class EfMonitorSettingsChangeLog(MarketMonitorDbContext db) : IMonitorSettingsChangeLog
{
    public void Record(MonitorSettingsChangeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        db.MonitorSettingsChangeLog.Add(new MonitorSettingsChangeRow
        {
            Id = Guid.NewGuid(),
            Actor = entry.Actor,
            ChangeType = entry.ChangeType.ToString(),
            Reason = entry.Reason,
            ChangedAt = entry.ChangedAt,
            Before = entry.Before,
            After = entry.After,
        });
        db.SaveChanges();
    }

    public IReadOnlyList<MonitorSettingsChangeEntry> GetHistory()
    {
        return [.. db.MonitorSettingsChangeLog
            .OrderByDescending(r => r.ChangedAt)
            .Select(r => new MonitorSettingsChangeEntry(
                r.Actor,
                Enum.Parse<MonitorSettingsChangeType>(r.ChangeType),
                r.Reason,
                r.ChangedAt,
                r.Before,
                r.After))];
    }
}
