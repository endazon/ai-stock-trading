using System.Collections.Concurrent;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;

// FR-12: ペーパートレード用の仮想ブローカー。板寄せせず参照価格（OrderIntent.Price）で即時全量約定する。
// 判断・記録・報告のフローは実発注（moomoo アダプタ）と完全に同一とする。
public sealed class PaperBrokerAdapter : IBrokerAdapter
{
    private readonly ConcurrentDictionary<string, BrokerOrder> _orders = new();
    private readonly TimeProvider _timeProvider;

    public PaperBrokerAdapter(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var order = new BrokerOrder(
            OrderId: Guid.NewGuid().ToString("N"),
            Intent: intent,
            Status: OrderStatus.Filled,
            FilledQuantity: intent.Quantity,
            AveragePrice: intent.Price,
            PlacedAt: now,
            CompletedAt: now);

        _orders[order.OrderId] = order;
        return Task.FromResult(order);
    }

    public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        _orders.TryGetValue(orderId, out var order);
        return Task.FromResult(order);
    }

    public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        // 本アダプタは PlaceOrderAsync で常に即時 Filled（終端状態）にするため、非終端状態の注文は存在せず
        // 取消は必ず下の終端チェックで弾かれる。IBrokerAdapter の契約を満たすための実装であり、
        // 非同期約定で非終端状態を持つ実発注アダプタ（moomoo 等）は、状態遷移に応じた並行制御を各自で実装する。
        if (!_orders.TryGetValue(orderId, out var order))
        {
            throw new InvalidOperationException($"注文が見つからない: {orderId}");
        }

        if (order.Status is OrderStatus.Filled or OrderStatus.Cancelled)
        {
            throw new InvalidOperationException($"終了状態（{order.Status}）の注文は取消できない: {orderId}");
        }

        _orders[orderId] = order with
        {
            Status = OrderStatus.Cancelled,
            CompletedAt = _timeProvider.GetUtcNow(),
        };
        return Task.CompletedTask;
    }
}
