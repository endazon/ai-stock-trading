using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Ports;

// FR-05, FR-12, ADR-0002: 証券会社アダプタのポート。moomoo・将来の他社・ペーパートレードを
// 実装差し替えで切り替える（platform ADR-0018 のポート規約に準拠）
public interface IBrokerAdapter
{
    /// <summary>注文を送信する。リスク管理の承認済み注文のみ渡すこと。</summary>
    Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken cancellationToken = default);

    /// <summary>注文IDで注文状態を照会する。未知のIDは null を返す。</summary>
    Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken cancellationToken = default);

    /// <summary>未約定の注文を取り消す。約定済み・未知のIDは <see cref="InvalidOperationException"/>。</summary>
    Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default);
}
