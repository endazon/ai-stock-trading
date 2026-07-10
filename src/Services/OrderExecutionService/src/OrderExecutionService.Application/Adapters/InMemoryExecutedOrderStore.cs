using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Domain;

namespace AiStockTrading.OrderExecution.Application.Adapters;

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
}
