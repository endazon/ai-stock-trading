using AiStockTrading.Configuration.Application.Ports;
using AiStockTrading.Configuration.Application.State;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.Configuration.Worker.Foundation.Persistence;

// FR-17, ADR-0007: 前提条件変更履歴の EF 実装（追記専用・新しい順）。
internal sealed class EfAssumptionsChangeLog(ConfigurationDbContext db) : IAssumptionsChangeLog
{
    public void Record(AssumptionsChangeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        db.AssumptionsChangeLog.Add(new AssumptionsChangeRow
        {
            Id = Guid.NewGuid(),
            Actor = entry.Actor,
            Reason = entry.Reason,
            ChangedAt = entry.ChangedAt,
            Version = entry.Version,
            Before = entry.Before,
            After = entry.After,
        });
        db.SaveChanges();
    }

    public IReadOnlyList<AssumptionsChangeEntry> GetHistory() =>
        [.. db.AssumptionsChangeLog
            .OrderByDescending(r => r.ChangedAt)
            .Select(r => new AssumptionsChangeEntry(r.Actor, r.Reason, r.ChangedAt, r.Version, r.Before, r.After))];
}
