using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Domain;

/// <summary>
/// FR-10, FR-12, ADR-0040 決定1, #819, IADR-0342 決定4: 承認が運ぶ損切りの実行機構を、発注先と注文の向きから
/// <b>実際に適用する扱い</b>へ解決する純関数。発注執行で手法を解釈する場所はここ 1 か所に限る。
/// <para>
/// 判定の順序が統制そのものである（上から先に当たったものが効く）:
/// </para>
/// <list type="number">
///   <item>手法が S0 → <see cref="StopLossMethodDisposition.BrokerStopOrder"/>（現行挙動。発注先を問わない）</item>
///   <item>発注先が moomoo SIMULATE でない → <see cref="StopLossMethodDisposition.Refused"/>
///     （<b>S0 以外は SIMULATE でしか選べない</b>。実弾で無防備な建玉を作らないため、S0 へ読み替えず発注しない）</item>
///   <item>空売りのエントリー → <see cref="StopLossMethodDisposition.BrokerStopOrder"/>
///     （本決定は空売り建玉に及ばない。ADR-0016 決定2(b) が独立に効く）</item>
///   <item>S2 → <see cref="StopLossMethodDisposition.ProtectiveStopWaived"/></item>
///   <item>S1 → <see cref="StopLossMethodDisposition.SoftwareStop"/>（#820, IADR-0344 決定2）</item>
///   <item>S3 / 未知の値 → <see cref="StopLossMethodDisposition.NotImplementedFallbackToBrokerStop"/>
///     （未実装。<b>緩い側へ倒さず S0 と同じ扱い</b>にする）</item>
/// </list>
/// <para>
/// <b>Close（手仕舞い）には適用しない</b>——呼び出し側が Open に限って呼ぶ。手法は保護レグの扱いであり、
/// 手仕舞いは保護レグを持たない。
/// </para>
/// </summary>
public static class StopLossMethodPolicy
{
    public static StopLossMethodDisposition Resolve(
        StopLossExecutionMethod method, OrderIntent entry, BrokerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (method == StopLossExecutionMethod.BrokerStopOrder)
        {
            return StopLossMethodDisposition.BrokerStopOrder;
        }

        if (provider != BrokerProvider.MoomooSimulate)
        {
            return StopLossMethodDisposition.Refused;
        }

        if (entry.ProductType == ProductType.ShortSell)
        {
            return StopLossMethodDisposition.BrokerStopOrder;
        }

        return method switch
        {
            StopLossExecutionMethod.NoProtectiveStop => StopLossMethodDisposition.ProtectiveStopWaived,
            StopLossExecutionMethod.SoftwareStop => StopLossMethodDisposition.SoftwareStop,
            _ => StopLossMethodDisposition.NotImplementedFallbackToBrokerStop,
        };
    }
}

/// <summary>#819, IADR-0342 決定4: 手法の解決結果。</summary>
public enum StopLossMethodDisposition
{
    /// <summary>S0: 保護逆指値を同時発注し、未受理なら建玉を持たない（IADR-0210）。</summary>
    BrokerStopOrder,

    /// <summary>S0 以外が SIMULATE 以外の発注先で届いた。発注しない（見送り・Error ログ）。</summary>
    Refused,

    /// <summary>S2: 保護逆指値を発注せず建玉を保持し、免除の事実を発行する。</summary>
    ProtectiveStopWaived,

    /// <summary>S3 / 未知: 未実装のため S0 と同じ扱い（警告ログ）。</summary>
    NotImplementedFallbackToBrokerStop,

    /// <summary>
    /// S1（#820, IADR-0344）: ブローカーへ保護レグを出さず、発注執行がソフトウェア逆指値を永続化する。
    /// 損切りライン到達（<c>StopLossTriggered</c>）で成行決済する。
    /// </summary>
    SoftwareStop,
}
