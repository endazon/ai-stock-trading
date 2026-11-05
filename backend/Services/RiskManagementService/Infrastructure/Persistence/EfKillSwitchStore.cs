using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using RiskManagementService.Features.RiskManagement;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-10, ADR-0003: kill switch 状態の EF 実装（単一行）。
public sealed class EfKillSwitchStore(RiskManagementDbContext db) : IKillSwitchStore
{
    public KillSwitchState GetState()
    {
        var row = db.KillSwitch.Find(SingletonKeys.Id);
        return row is null
            ? KillSwitchState.Disengaged
            : new KillSwitchState(row.Engaged, row.Actor, row.Reason, row.ChangedAt);
    }

    public void SetState(KillSwitchState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var row = db.KillSwitch.Find(SingletonKeys.Id);
        if (row is null)
        {
            db.KillSwitch.Add(new KillSwitchRow
            {
                Id = SingletonKeys.Id,
                Engaged = state.Engaged,
                Actor = state.Actor,
                Reason = state.Reason,
                ChangedAt = state.ChangedAt,
            });
        }
        else
        {
            row.Engaged = state.Engaged;
            row.Actor = state.Actor;
            row.Reason = state.Reason;
            row.ChangedAt = state.ChangedAt;
        }

        db.SaveChanges();
    }

    // 🔴 #1534, IADR-0620: 比較と書き込みを 1 つの取引で行う（関係 DB では直列化可能。並行する他方は取引の確定で失敗し、競合として false を返す）。
    public bool TrySetState(KillSwitchState state, string expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var tx = db.Database.IsRelational()
            ? db.Database.BeginTransaction(System.Data.IsolationLevel.Serializable)
            : null;
        try
        {
            var row = db.KillSwitch.Find(SingletonKeys.Id);
            var current = row is null
                ? KillSwitchState.Disengaged
                : new KillSwitchState(row.Engaged, row.Actor, row.Reason, row.ChangedAt);
            if (!string.Equals(current.Version, expectedVersion, StringComparison.Ordinal))
                return false;

            SetState(state);
            tx?.Commit();
            return true;
        }
        catch (DbUpdateException) when (tx is not null)
        {
            return false;
        }
        catch (InvalidOperationException ex) when (tx is not null && ex.InnerException is DbException)
        {
            return false;
        }
    }
}
