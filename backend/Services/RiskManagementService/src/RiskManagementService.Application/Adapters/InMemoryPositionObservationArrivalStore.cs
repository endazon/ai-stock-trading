using AiStockTrading.RiskManagement.Application.Ports;

namespace AiStockTrading.RiskManagement.Application.Adapters;

// FR-21, FR-10, FR-06, #463, IADR-0181: 観測の到達（最終観測時刻）のインメモリ実装。
//
// **本番では使わない。** 本ストアは「観測が一度も届いていない（異常）」と「観測して 0 件だった（正常）」を
// 分けるためのものであり、プロセス内に持つと再起動で前者へ戻る——供給されていた件数が未供給へ化ける。
// 本番は <c>EfPositionObservationArrivalStore</c>（永続）を配線する。
public sealed class InMemoryPositionObservationArrivalStore : IPositionObservationArrivalStore
{
    private readonly Lock _gate = new();
    private DateTimeOffset? _lastObservedAt;

    public DateTimeOffset? GetLastObservedAt()
    {
        lock (_gate)
        {
            return _lastObservedAt;
        }
    }

    public void Record(DateTimeOffset observedAt)
    {
        lock (_gate)
        {
            // **単調前進のみ**（EF 実装と同じ規律。後着の古い観測で巻き戻さない）。
            if (_lastObservedAt is null || observedAt > _lastObservedAt)
            {
                _lastObservedAt = observedAt;
            }
        }
    }
}
