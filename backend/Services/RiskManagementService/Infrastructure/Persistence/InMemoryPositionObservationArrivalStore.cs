using RiskManagementService.Features.RiskManagement;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-21, FR-10, FR-06, #463, IADR-0181: 観測が届いた取引日のインメモリ実装。
//
// **本番では使わない。** 本ストアは「観測が一度も届いていない（異常）」と「観測して 0 件だった（正常）」を
// 分けるためのものであり、プロセス内に持つと再起動で前者へ戻る。本番は EF 実装（永続）を配線する。
public sealed class InMemoryPositionObservationArrivalStore : IPositionObservationArrivalStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<DateOnly, DateTimeOffset> _days = [];

    public void Record(DateOnly tradingDay, DateTimeOffset observedAt)
    {
        lock (_gate)
        {
            // 同一取引日の複数観測では最新を保つ（判定に使うのは**キーの存在**である）。
            if (!_days.TryGetValue(tradingDay, out var existing) || observedAt > existing)
            {
                _days[tradingDay] = observedAt;
            }
        }
    }

    public IReadOnlyList<DateOnly> GetObservedDaysBetween(DateOnly fromInclusive, DateOnly toInclusive)
    {
        lock (_gate)
        {
            return [.. _days.Keys.Where(d => d >= fromInclusive && d <= toInclusive).Order()];
        }
    }
}
