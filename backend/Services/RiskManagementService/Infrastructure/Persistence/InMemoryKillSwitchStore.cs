using RiskManagementService.Features.RiskManagement;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-10: kill switch 状態のインメモリ実装。初期は未起動。PostgreSQL 永続化は Slice B で差し替える。
public sealed class InMemoryKillSwitchStore : IKillSwitchStore
{
    private readonly Lock _gate = new();
    private KillSwitchState _state = KillSwitchState.Disengaged;

    public KillSwitchState GetState()
    {
        lock (_gate)
        {
            return _state;
        }
    }

    public void SetState(KillSwitchState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            _state = state;
        }
    }

    // #1534, IADR-0620: 比較と書き込みを同じ錠の中で行う。
    public bool TrySetState(KillSwitchState state, string expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            if (!string.Equals(_state.Version, expectedVersion, StringComparison.Ordinal))
                return false;
            _state = state;
            return true;
        }
    }
}
