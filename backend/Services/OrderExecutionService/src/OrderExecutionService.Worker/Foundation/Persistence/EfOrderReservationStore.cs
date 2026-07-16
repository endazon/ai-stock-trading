using AiStockTrading.OrderExecution.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.OrderExecution.Worker.Foundation.Persistence;

// #131, FR-05, IADR-0057: 発注前 DecisionId 予約ストアの EF 実装。
// TryReserve はブローカ発注より前に呼ばれ、SaveChanges で「コミットしてから」true を返す（発注前予約の要）。
internal sealed class EfOrderReservationStore(OrderExecutionDbContext db) : IOrderReservationStore
{
    public bool TryReserve(Guid decisionId, DateTimeOffset reservedAt)
    {
        // 先読みは高速路（再配送の大半はここで false）。並行配送の実際の排他は主キーの一意制約が担う。
        if (db.DispatchReservations.Any(r => r.DecisionId == decisionId))
            return false;

        db.DispatchReservations.Add(new OrderDispatchReservationRow
        {
            DecisionId = decisionId,
            State = OrderDispatchState.Reserved,
            ReservedAt = reservedAt,
        });

        try
        {
            db.SaveChanges();
            return true;
        }
        catch (DbUpdateException)
        {
            // 一意制約違反＝他プロセスが先に予約を確保した。書き込み失敗一般もここに落ちるが、いずれも
            // 「予約を確保できていない」ことに変わりはなく、false（＝発注しない）が安全側（IADR-0057）。
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public void MarkCompleted(Guid decisionId, string brokerOrderId, DateTimeOffset completedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(brokerOrderId);

        var row = db.DispatchReservations.FirstOrDefault(r => r.DecisionId == decisionId)
            ?? throw new InvalidOperationException($"DecisionId={decisionId} の予約がありません。");

        row.State = OrderDispatchState.Completed;
        row.BrokerOrderId = brokerOrderId;
        row.CompletedAt = completedAt;
        db.SaveChanges();
    }

    public OrderDispatchReservation? Find(Guid decisionId)
    {
        var row = db.DispatchReservations.AsNoTracking().FirstOrDefault(r => r.DecisionId == decisionId);
        return row is null
            ? null
            : new OrderDispatchReservation(row.DecisionId, row.State, row.ReservedAt, row.BrokerOrderId);
    }
}
