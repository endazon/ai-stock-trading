using OrderExecutionService.Application.Ports;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Application.Adapters;

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
