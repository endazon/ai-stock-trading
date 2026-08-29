using RiskManagementService.Features.RiskManagement;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-10, IADR-0008: 日次損失ロックアウト状態の EF 実装（単一行。不在=ロックなし）。
public sealed class EfLockoutStore(RiskManagementDbContext db) : ILockoutStore
{
    public LockoutState? Get()
    {
        var row = db.Lockout.Find(SingletonKeys.Id);
        return row is null
            ? null
            : new LockoutState(row.ReleaseOn, row.Reason, row.EngagedAt);
    }

    public void Set(LockoutState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var row = db.Lockout.Find(SingletonKeys.Id);
        if (row is null)
        {
            db.Lockout.Add(new LockoutRow
            {
                Id = SingletonKeys.Id,
                ReleaseOn = state.ReleaseOn,
                Reason = state.Reason,
                EngagedAt = state.EngagedAt,
            });
        }
        else
        {
            row.ReleaseOn = state.ReleaseOn;
            row.Reason = state.Reason;
            row.EngagedAt = state.EngagedAt;
        }

        db.SaveChanges();
    }

    public void Clear()
    {
        var row = db.Lockout.Find(SingletonKeys.Id);
        if (row is not null)
        {
            db.Lockout.Remove(row);
            db.SaveChanges();
        }
    }
}
