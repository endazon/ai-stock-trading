using RiskManagementService.Application.Ports;
using RiskManagementService.Application.State;

namespace RiskManagementService.Application.Adapters;

// FR-10, IADR-0008: 日次損失ロックアウト状態のインメモリ実装。PostgreSQL 永続化は Slice B で差し替える。
public sealed class InMemoryLockoutStore : ILockoutStore
{
    private readonly Lock _gate = new();
    private LockoutState? _state;

    public LockoutState? Get()
    {
        lock (_gate)
        {
            return _state;
        }
    }

    public void Set(LockoutState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            _state = state;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _state = null;
        }
    }
}
