using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Ports;

// FR-10, UC-02, ADR-0016 決定2(b), #331, IADR-0210: 保護注文（逆指値・成行手仕舞い）の能力ポート。
// 損切りの実行機構はブローカー側の逆指値であり（planning#88 裁定）、エントリー（Open）と同時に
// 本ポートで逆指値を発注する。**実装しないブローカーでは Open 注文が見送られる**（逆指値なしの
// 建玉を持たない・fail-closed。IADR-0210 決定 1）。IClientOrderIdBroker と同じ能力インターフェース方式。
public interface IProtectiveOrderBroker
{
    /// <summary>
    /// 逆指値（ストップ注文）を発注する。<paramref name="closeIntent"/> は決済方向
    /// （エントリーの反対売買・<see cref="PositionEffect.Close"/>）、<paramref name="triggerPrice"/> は
    /// 損切りライン（発火価格）。<paramref name="decisionId"/> は相関キー（moomoo は remark へ伝播）。
    /// 受理されなかった場合は <see cref="OrderStatus.Rejected"/> の終端注文を返す
    /// （呼び出し側が建玉解消の分岐に入る）。接続確立の失敗は
    /// <see cref="BrokerUnavailableException"/>。
    /// </summary>
    Task<BrokerOrder> PlaceStopOrderAsync(
        OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 成行の手仕舞い注文を発注する（逆指値が未受理・失効で再発注もできない場合の建玉解消。
    /// 業務フロー 02「逆指値が成立しない場合の扱い」）。<paramref name="closeIntent"/>.Price は
    /// 参照価格（paper の約定価格・スリッページ基準）であり、実ブローカーでは成行として扱う。
    /// </summary>
    Task<BrokerOrder> PlaceMarketOrderAsync(
        OrderIntent closeIntent, Guid decisionId, CancellationToken cancellationToken = default);
}
