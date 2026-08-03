using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.OrderExecution.Infrastructure.Foundation.Persistence;

// #154, FR-05, FR-19, IADR-0067: 注文の訂正・取消台帳の EF 実装（追記専用・専有 DB）。
internal sealed class EfOrderLifecycleStore(OrderExecutionDbContext db) : IOrderLifecycleStore
{
    public void Append(OrderLifecycleEvent lifecycleEvent)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);

        db.OrderLifecycleEvents.Add(new OrderLifecycleEventRow
        {
            Id = lifecycleEvent.Id,
            DecisionId = lifecycleEvent.DecisionId,
            OrderId = lifecycleEvent.OrderId,
            Kind = lifecycleEvent.Kind,
            PreviousQuantity = lifecycleEvent.PreviousQuantity,
            PreviousPrice = lifecycleEvent.PreviousPrice,
            Quantity = lifecycleEvent.Quantity,
            Price = lifecycleEvent.Price,
            Reason = lifecycleEvent.Reason,
            OccurredAt = lifecycleEvent.OccurredAt,
        });
        db.SaveChanges();
    }

    public IReadOnlyList<OrderLifecycleEvent> GetByOrderId(string orderId) =>
        db.OrderLifecycleEvents
            .Where(r => r.OrderId == orderId)
            .OrderBy(r => r.OccurredAt)
            .Select(r => new OrderLifecycleEvent(
                r.Id, r.DecisionId, r.OrderId, r.Kind,
                r.PreviousQuantity, r.PreviousPrice, r.Quantity, r.Price, r.Reason, r.OccurredAt))
            .ToList();
}
