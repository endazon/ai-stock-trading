using AiStockTrading.OrderExecution.Application.Ports;

namespace AiStockTrading.OrderExecution.Application.Adapters;

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
}
