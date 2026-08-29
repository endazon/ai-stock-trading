using RiskManagementService.Features.RiskManagement;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-10, ADR-0009: 取引の一時停止（pause）状態の EF 実装（単一行）。kill switch（EfKillSwitchStore）と同型。
public sealed class EfPauseStore(RiskManagementDbContext db) : IPauseStore
{
    public PauseState GetState()
    {
        var row = db.Pause.Find(SingletonKeys.Id);
        return row is null
            ? PauseState.NotPaused
            : new PauseState(row.Paused, row.Actor, row.Reason, row.ChangedAt);
    }

    public void SetState(PauseState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var row = db.Pause.Find(SingletonKeys.Id);
        if (row is null)
        {
            db.Pause.Add(new PauseRow
            {
                Id = SingletonKeys.Id,
                Paused = state.Paused,
                Actor = state.Actor,
                Reason = state.Reason,
                ChangedAt = state.ChangedAt,
            });
        }
        else
        {
            row.Paused = state.Paused;
            row.Actor = state.Actor;
            row.Reason = state.Reason;
            row.ChangedAt = state.ChangedAt;
        }

        db.SaveChanges();
    }
}
