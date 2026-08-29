using OrderExecutionService.Features.OrderExecution;

namespace OrderExecutionService.Infrastructure.Persistence;

// #131, FR-05, IADR-0057: 発注予約ストアのインメモリ実装（dev/test 用）。実運用は EF 実装（一意制約が権威）。
public sealed class InMemoryOrderReservationStore : IOrderReservationStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, OrderDispatchReservation> _reservations = [];

    public bool TryReserve(Guid decisionId, DateTimeOffset reservedAt)
    {
        lock (_gate)
        {
            return _reservations.TryAdd(
                decisionId,
                new OrderDispatchReservation(decisionId, OrderDispatchState.Reserved, reservedAt, BrokerOrderId: null));
        }
    }

    public void MarkCompleted(Guid decisionId, string brokerOrderId, DateTimeOffset completedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(brokerOrderId);
        lock (_gate)
        {
            if (!_reservations.TryGetValue(decisionId, out var reservation))
                throw new InvalidOperationException($"DecisionId={decisionId} の予約がありません。");

            _reservations[decisionId] = reservation with
            {
                State = OrderDispatchState.Completed,
                BrokerOrderId = brokerOrderId,
                // #137, IADR-0059: 確定時刻はパージの述語に用いる（EF 実装と同じく保持する）。
                CompletedAt = completedAt,
            };
        }
    }

    public OrderDispatchReservation? Find(Guid decisionId)
    {
        lock (_gate)
        {
            return _reservations.GetValueOrDefault(decisionId);
        }
    }

    // #141, IADR-0074: 滞留 Reserved（reservedBefore より古い Reserved）を ReservedAt 昇順で最大 batchSize 件返す。
    public IReadOnlyList<OrderDispatchReservation> FindStalledReserved(DateTimeOffset reservedBefore, int batchSize)
    {
        lock (_gate)
        {
            return _reservations.Values
                .Where(r => r.State == OrderDispatchState.Reserved && r.ReservedAt < reservedBefore)
                .OrderBy(r => r.ReservedAt)
                .Take(batchSize)
                .ToList();
        }
    }

    // #141, IADR-0074: 未発注と確定した Reserved 予約のみ削除する。終端行（Completed）は決して消さない安全ガード。
    public bool Release(Guid decisionId)
    {
        lock (_gate)
        {
            if (!_reservations.TryGetValue(decisionId, out var reservation)
                || reservation.State != OrderDispatchState.Reserved)
                return false;

            _reservations.Remove(decisionId);
            return true;
        }
    }

    // NFR（運用）, #137, IADR-0059: 終端（Completed）かつ cutoff より古い行のみをバッチ削除する。
    // Reserved は「発注済みか不明」であり、どれだけ古くても対象にしない（二重発注の防止が最優先）。
    public int PurgeCompletedBefore(DateTimeOffset cutoff, int batchSize)
    {
        lock (_gate)
        {
            var expired = _reservations.Values
                .Where(r => r.State == OrderDispatchState.Completed && r.CompletedAt < cutoff)
                .Take(batchSize)
                .Select(r => r.DecisionId)
                .ToList();

            foreach (var decisionId in expired)
                _reservations.Remove(decisionId);

            return expired.Count;
        }
    }
}
