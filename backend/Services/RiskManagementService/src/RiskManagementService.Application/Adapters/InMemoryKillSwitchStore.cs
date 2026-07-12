using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;

namespace AiStockTrading.RiskManagement.Application.Adapters;

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
}
