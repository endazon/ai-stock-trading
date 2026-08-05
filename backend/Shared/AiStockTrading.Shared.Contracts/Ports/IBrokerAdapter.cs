using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Ports;

// FR-05, FR-12, ADR-0002: 証券会社アダプタのポート。moomoo・将来の他社・ペーパートレードを
// 実装差し替えで切り替える（platform ADR-0018 のポート規約に準拠）
public interface IBrokerAdapter
{
    /// <summary>
    /// FR-20, FR-12, #386, IADR-0149 決定1: <b>このアダプタが実際に発注する先</b>。
    /// <para>
    /// Stage 1 の合格判定は「<c>SIMULATE</c> の約定のみで集計し、内蔵 <c>paper</c> の約定を算入してはならない」
    /// （FR-20）。その判別に使える唯一正しい情報は<b>どのアダプタが発注したか</b>である——
    /// 取引判断が運ぶ <c>OrderIntent.Mode</c> は<b>段階が定める既定の発注先</b>であって現在の発注先ではない
    /// （IADR-0140 決定3・決定4）。
    /// </para>
    /// </summary>
    BrokerProvider Provider { get; }

    /// <summary>注文を送信する。リスク管理の承認済み注文のみ渡すこと。</summary>
    Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken cancellationToken = default);

    /// <summary>注文IDで注文状態を照会する。未知のIDは null を返す。</summary>
    Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken cancellationToken = default);

    /// <summary>未約定の注文を取り消す。約定済み・未知のIDは <see cref="InvalidOperationException"/>。</summary>
    Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default);
}
