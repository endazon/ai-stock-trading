using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;

namespace OrderExecutionService.Tests;

// NFR-09, #1051, IADR-0444 決定3: 発注先（取引環境）だけを差し替えたブローカー。リコンサイラは照会先の取引環境を
// 発注アダプタの Provider で知る（照会は発注と同じ接続を共有する）ため、門の評価を SIMULATE / 実弾で試すのに使う。
// 発注・照会・取消は内蔵 paper へそのまま委ねる（リコンサイラ本体はブローカーへ書かない）。
public sealed class ProviderOverrideBroker(BrokerProvider provider, IBrokerAdapter? inner = null) : IBrokerAdapter
{
    private readonly IBrokerAdapter _inner = inner ?? new PaperBrokerAdapter();

    public BrokerProvider Provider { get; } = provider;

    public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken cancellationToken = default) =>
        _inner.PlaceOrderAsync(intent, cancellationToken);

    public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
        _inner.GetOrderAsync(orderId, cancellationToken);

    public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
        _inner.CancelOrderAsync(orderId, cancellationToken);
}
