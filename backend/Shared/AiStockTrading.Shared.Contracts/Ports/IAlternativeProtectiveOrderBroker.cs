using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Ports;

// FR-10, FR-12, UC-02, ADR-0040 決定1（S3）, #821, IADR-0347: 保護レグを**ブローカー側の別の注文種別**で
// 発注する能力ポート（S3）。IProtectiveOrderBroker の姉妹であり、実装するのは moomoo アダプタだけである
// （S3 は moomoo SIMULATE にしか届かない。IADR-0342 決定4）。
//
// 🔴 **本ポートの存在理由は「拒否理由を持ち帰ること」である。** 公式は模擬取引を「指値・成行のみ」としており、
// S3 は拒否される見込みが高い。拒否理由（retType / retMsg）をログだけに残すと、後から
// 「なぜ S3 が使えないのか」を監査台帳から読めない —— 戻り値へ載せて監査イベントまで運ぶ（#821 の目的そのもの）。
public interface IAlternativeProtectiveOrderBroker
{
    /// <summary>本アダプタが S3 で試す注文種別（構成で決まる）。発注前に知れる必要がある（発注が例外で落ちても記録するため）。</summary>
    AlternativeProtectiveOrderType AlternativeProtectiveOrderType { get; }

    /// <summary>
    /// 保護レグを <see cref="AlternativeProtectiveOrderType"/> の注文種別で発注する。
    /// <paramref name="closeIntent"/> は決済方向（エントリーの反対売買・<see cref="PositionEffect.Close"/>）、
    /// <paramref name="triggerPrice"/> は損切りライン（発火価格）、<paramref name="entryReferencePrice"/> は
    /// エントリーの判断価格（トレール幅の基準。発火価格だけではトレール幅が決まらない）。
    /// <para>
    /// 受理されなかった場合は <see cref="OrderStatus.Rejected"/> の終端注文と**拒否理由**を返す
    /// （呼び出し側は S0 とまったく同じ建玉解消の分岐へ入る）。接続確立の失敗は
    /// <see cref="BrokerUnavailableException"/>（＝確実に未発注）であり、本メソッドは丸めない。
    /// </para>
    /// </summary>
    Task<AlternativeProtectiveOrderPlacement> PlaceAlternativeStopOrderAsync(
        OrderIntent closeIntent,
        decimal triggerPrice,
        decimal entryReferencePrice,
        Guid decisionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// #821, IADR-0347: S3 の発注 1 回の結果。<b>Events 名前空間には置かない</b>——契約イベントではなくポートの戻り値である
/// （Events 名前空間の record は EventTypeDiscovery がドメインイベントとして拾う）。
/// <para>
/// <c>RejectReasonCode</c> は moomoo の <c>retType</c>（成功は 0。送信前に棄却した場合は null）、
/// <c>RejectReasonMessage</c> は <c>retMsg</c>（または送信前棄却・送信後例外の理由）。
/// **受理された場合は両方 null** である。
/// </para>
/// <para>
/// #842, IADR-0405: <c>BrokerOrderId</c> は**ブローカーが採番した注文 ID** である。送信前に棄却した場合と、
/// ブローカーが受理しなかった（<c>retType=-1</c>）場合は **null** —— <c>Order.OrderId</c> はアダプタが合成した値であり、
/// 7 年保持の監査台帳へ「証券会社へ問い合わせても存在しない注文 ID」を残さないために分けて持つ。
/// </para>
/// </summary>
public sealed record AlternativeProtectiveOrderPlacement(
    BrokerOrder Order,
    AlternativeProtectiveOrderType OrderType,
    int? RejectReasonCode,
    string? RejectReasonMessage,
    string? BrokerOrderId);
