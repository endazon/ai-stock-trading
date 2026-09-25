using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Infrastructure.Persistence;

// FR-05: 発注結果ストアのインメモリ実装。PostgreSQL 永続化は Slice B で差し替える。
public sealed class InMemoryExecutedOrderStore : IExecutedOrderStore
{
    private readonly Lock _gate = new();
    private readonly List<ExecutionRecord> _records = [];

    public void Save(ExecutionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            _records.Add(record);
        }
    }

    public IReadOnlyList<ExecutionRecord> GetAll()
    {
        lock (_gate)
        {
            return _records.AsEnumerable().Reverse().ToList();
        }
    }

    public ExecutionRecord? FindByDecisionId(Guid decisionId)
    {
        lock (_gate)
        {
            return _records.FirstOrDefault(r => r.DecisionId == decisionId);
        }
    }

    // #270, IADR-0113: 追跡対象＝非終端かつ追跡上限内の記録を古い順に返す。
    public IReadOnlyList<ExecutionRecord> FindPendingSince(DateTimeOffset since, int batchSize)
    {
        lock (_gate)
        {
            return _records
                .Where(r => OrderStatusLifecycle.IsPending(r.Status) && r.ExecutedAt >= since)
                .OrderBy(r => r.ExecutedAt)
                .Take(batchSize)
                .ToList();
        }
    }

    // FR-10, #958, IADR-0406 決定1: 指定した注文 ID の非終端の記録を古い順に返す（追跡上限は見ない）。
    public IReadOnlyList<ExecutionRecord> FindPendingByOrderIds(IReadOnlyCollection<string> orderIds)
    {
        ArgumentNullException.ThrowIfNull(orderIds);
        if (orderIds.Count == 0)
            return [];

        lock (_gate)
        {
            return _records
                .Where(r => OrderStatusLifecycle.IsPending(r.Status) && orderIds.Contains(r.OrderId))
                .OrderBy(r => r.ExecutedAt)
                .ToList();
        }
    }

    // FR-10, #958, IADR-0406 決定3: 非終端の記録の追跡の起点を進める（時刻だけを書く）。
    public bool RenewTracking(string orderId, DateTimeOffset trackedFrom)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        lock (_gate)
        {
            var index = _records.FindIndex(r => r.OrderId == orderId);
            if (index < 0
                || !OrderStatusLifecycle.IsPending(_records[index].Status)
                || _records[index].ExecutedAt >= trackedFrom)
            {
                return false;
            }

            _records[index] = _records[index] with { ExecutedAt = trackedFrom };
            return true;
        }
    }

    // #270, IADR-0113: 観測した最新状態を既存記録へ反映する（無ければ何もしない＝新規に作らない）。
    public bool UpdateOutcome(
        string orderId,
        OrderStatus status,
        int filledQuantity,
        decimal averagePrice,
        decimal slippageRatio,
        DateTimeOffset executedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        lock (_gate)
        {
            var index = _records.FindIndex(r => r.OrderId == orderId);
            if (index < 0)
                return false;

            _records[index] = _records[index] with
            {
                Status = status,
                FilledQuantity = filledQuantity,
                AveragePrice = averagePrice,
                SlippageRatio = slippageRatio,
                ExecutedAt = executedAt,
            };
            return true;
        }
    }
}
