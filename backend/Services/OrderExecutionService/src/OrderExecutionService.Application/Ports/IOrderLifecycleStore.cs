using AiStockTrading.OrderExecution.Domain;

namespace AiStockTrading.OrderExecution.Application.Ports;

// FR-05, FR-19, FR-11, IADR-0067: 注文の訂正・取消の永続化（追記専用）。実運用では PostgreSQL。
public interface IOrderLifecycleStore
{
    void Append(OrderLifecycleEvent lifecycleEvent);

    /// <summary>指定注文の訂正・取消を発生順に返す（照会・リコンサイル #141 用）。</summary>
    IReadOnlyList<OrderLifecycleEvent> GetByOrderId(string orderId);
}
