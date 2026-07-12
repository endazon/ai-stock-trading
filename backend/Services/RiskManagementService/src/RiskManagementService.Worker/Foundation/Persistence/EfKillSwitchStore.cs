using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;

namespace AiStockTrading.RiskManagement.Worker.Foundation.Persistence;

// FR-10, ADR-0003: kill switch 状態の EF 実装（単一行）。
internal sealed class EfKillSwitchStore(RiskManagementDbContext db) : IKillSwitchStore
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
}
