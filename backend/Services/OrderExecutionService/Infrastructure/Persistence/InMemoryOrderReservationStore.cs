using OrderExecutionService.Features.OrderExecution;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Infrastructure.Persistence;

// #131, FR-05, IADR-0057: 発注予約ストアのインメモリ実装（dev/test 用）。実運用は EF 実装（一意制約が権威）。
public sealed class InMemoryOrderReservationStore : IOrderReservationStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, OrderDispatchReservation> _reservations = [];

    public bool TryReserve(Guid decisionId, DateTimeOffset reservedAt, BrokerProvider? brokerProvider)
    {
        lock (_gate)
        {
            // #1051, IADR-0444 決定1: 送る先の取引環境を予約に残す（EF 実装と同じ）。
            return _reservations.TryAdd(
                decisionId,
                new OrderDispatchReservation(
                    decisionId, OrderDispatchState.Reserved, reservedAt, BrokerOrderId: null,
                    BrokerProvider: brokerProvider));
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

    // 🔴 FR-05, FR-10, #876, IADR-0398: 予約を取る前の見送りを Forgone で記録する。既に在る行は変えない（EF 実装と同じ意味論）。
    public ForgoneRecordOutcome TryRecordForgone(Guid decisionId, DateTimeOffset forgoneAt)
    {
        lock (_gate)
        {
            if (_reservations.TryGetValue(decisionId, out var existing))
                return OutcomeOf(existing.State);

            _reservations[decisionId] = new OrderDispatchReservation(
                decisionId, OrderDispatchState.Forgone, forgoneAt, BrokerOrderId: null, CompletedAt: forgoneAt);
            return ForgoneRecordOutcome.Recorded;
        }
    }

    // 🔴 FR-05, FR-10, #876, IADR-0398: 自分が取った Reserved を Forgone へ移す。Completed は決して上書きしない。
    public ForgoneRecordOutcome MarkReservationForgone(Guid decisionId, DateTimeOffset forgoneAt)
    {
        lock (_gate)
        {
            if (!_reservations.TryGetValue(decisionId, out var reservation))
            {
                _reservations[decisionId] = new OrderDispatchReservation(
                    decisionId, OrderDispatchState.Forgone, forgoneAt, BrokerOrderId: null, CompletedAt: forgoneAt);
                return ForgoneRecordOutcome.Recorded;
            }

            if (reservation.State != OrderDispatchState.Reserved)
                return OutcomeOf(reservation.State);

            _reservations[decisionId] = reservation with { State = OrderDispatchState.Forgone, CompletedAt = forgoneAt };
            return ForgoneRecordOutcome.Recorded;
        }
    }

    // 🔴 未定義の状態は「発注済み」の側へ倒す（見送りを主張させない）。EF 実装と同じ。
    private static ForgoneRecordOutcome OutcomeOf(OrderDispatchState state) => state switch
    {
        OrderDispatchState.Forgone => ForgoneRecordOutcome.AlreadyForgone,
        OrderDispatchState.Reserved => ForgoneRecordOutcome.HeldByReservation,
        _ => ForgoneRecordOutcome.AlreadyCompleted,
    };

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
