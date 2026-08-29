using System.Collections.Concurrent;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Domain;

namespace OrderExecutionService.Infrastructure.Persistence;

// FR-05, FR-19, IADR-0067: 注文の訂正・取消のインメモリ台帳（追記専用）。実運用の EF 実装は Worker 層。
// 単体テスト・ローカル実行のための供給で、本番配線は EfOrderLifecycleStore（IADR-0067）。
public sealed class InMemoryOrderLifecycleStore : IOrderLifecycleStore
{
    private readonly ConcurrentQueue<OrderLifecycleEvent> _events = new();

    public void Append(OrderLifecycleEvent lifecycleEvent)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);
        _events.Enqueue(lifecycleEvent);
    }

    public IReadOnlyList<OrderLifecycleEvent> GetByOrderId(string orderId) =>
        _events.Where(e => e.OrderId == orderId).OrderBy(e => e.OccurredAt).ToList();
}
