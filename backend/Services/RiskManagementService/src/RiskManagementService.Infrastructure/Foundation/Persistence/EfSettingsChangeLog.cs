using RiskManagementService.Application.Ports;
using RiskManagementService.Application.State;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-11: 変更履歴の EF 実装（追記専用）。新しい順で照会する。
internal sealed class EfSettingsChangeLog(RiskManagementDbContext db) : ISettingsChangeLog
{
    public void Record(SettingsChangeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        db.SettingsChangeLog.Add(new SettingsChangeRow
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

    public IReadOnlyList<SettingsChangeEntry> GetHistory()
    {
        return [.. db.SettingsChangeLog
            .OrderByDescending(r => r.ChangedAt)
            .Select(r => new SettingsChangeEntry(
                r.Actor,
                Enum.Parse<SettingsChangeType>(r.ChangeType),
                r.Reason,
                r.ChangedAt,
                r.Before,
                r.After))];
    }
}
