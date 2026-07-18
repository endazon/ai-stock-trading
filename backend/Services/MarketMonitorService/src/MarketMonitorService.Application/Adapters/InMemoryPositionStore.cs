using AiStockTrading.MarketMonitor.Application.Ports;
using AiStockTrading.MarketMonitor.Domain;

namespace AiStockTrading.MarketMonitor.Application.Adapters;

// FR-03, FR-10: 保有ポジションのインメモリ実装。実運用の供給はリスク管理（#12/#63 台帳）を照会する HttpPositionStore が担い、本実装はシード／テスト用に用いる。
// 既定は空（保有なし＝損切り検知の対象なし）で安全側に倒す。
public sealed class InMemoryPositionStore : IPositionStore
{
    private readonly Lock _gate = new();
    private IReadOnlyCollection<HeldPosition> _positions = [];

    public Task<IReadOnlyCollection<HeldPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_positions);
        }
    }

    public void Set(IReadOnlyCollection<HeldPosition> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        lock (_gate)
        {
            _positions = positions;
        }
    }
}
