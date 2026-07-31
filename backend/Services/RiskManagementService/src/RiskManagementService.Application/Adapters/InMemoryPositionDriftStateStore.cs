using AiStockTrading.RiskManagement.Application.Ports;

namespace AiStockTrading.RiskManagement.Application.Adapters;

// FR-05, FR-10, #305, IADR-0124: 建玉乖離の追跡状態ストアのインメモリ実装（ユニット試験・非 relational 用）。
// durable な永続は EF 実装（EfPositionDriftStateStore）が担う。
//
// **版（Version）の意味論も実装する。** 単なる辞書にすると「並行更新に負ける」経路をテストで再現できず、
// 本ストアで緑になった実装が DB 上では別挙動、という乖離が生まれる。
public sealed class InMemoryPositionDriftStateStore : IPositionDriftStateStore
{
    private readonly Lock _gate = new();
    private PositionDriftState _state = PositionDriftState.Initial;

    public PositionDriftState Get()
    {
        lock (_gate)
        {
            return _state;
        }
    }

    public bool TrySave(PositionDriftState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (_gate)
        {
            if (state.Version != _state.Version)
            {
                return false;
            }

            _state = state with { Version = _state.Version + 1 };
            return true;
        }
    }
}
