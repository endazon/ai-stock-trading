using RiskManagementService.Application.Ports;
using RiskManagementService.Application.State;

namespace RiskManagementService.Application.Adapters;

// FR-10, ADR-0009: 一時停止（pause）状態のインメモリ実装。初期は非停止。PostgreSQL 永続化は EfPauseStore で差し替える。
public sealed class InMemoryPauseStore : IPauseStore
{
    private readonly Lock _gate = new();
    private PauseState _state = PauseState.NotPaused;

    public PauseState GetState()
    {
        lock (_gate)
        {
            return _state;
        }
    }

    public void SetState(PauseState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            _state = state;
        }
    }
}
