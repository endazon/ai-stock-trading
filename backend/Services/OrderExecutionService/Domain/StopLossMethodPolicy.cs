using AiStockTrading.Shared.Contracts.Events;
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
///   <item>S3 → <see cref="StopLossMethodDisposition.AlternativeBrokerOrderType"/>
///     （#821・IADR-0347。代替注文種別で保護レグを発注し、種別と拒否理由を監査へ残す。
///     <b>拒否・受理いずれの扱いも S0 と同じ</b>）</item>
///   <item>未知の値 → <see cref="StopLossMethodDisposition.NotImplementedFallbackToBrokerStop"/>
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
        StopLossExecutionMethod method, OrderIntent entry, BrokerProvider provider) =>
        ResolveWithReason(method, entry, provider).Disposition;

    /// <summary>
    /// FR-10, FR-06, #1002, IADR-0429 決定2: 解決結果と、<b>承認の手法と違う扱いになった理由</b>を同時に返す。
    /// 🔴 <b>判定の順序はここ 1 か所にだけ書く</b>（<see cref="Resolve"/> はこれを呼ぶだけ）。理由を別の関数で
    /// 導き直すと、順序を変えたときに片方だけが古くなる（例: 空売りかつ SIMULATE 以外は「拒否」であり「空売り」ではない）。
    /// </summary>
    public static StopLossMethodResolution ResolveWithReason(
        StopLossExecutionMethod method, OrderIntent entry, BrokerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (method == StopLossExecutionMethod.BrokerStopOrder)
        {
            return new(StopLossMethodDisposition.BrokerStopOrder, StopLossMethodResolutionReason.AsSelected);
        }

        if (provider != BrokerProvider.MoomooSimulate)
        {
            return new(StopLossMethodDisposition.Refused, StopLossMethodResolutionReason.BrokerNotMoomooSimulate);
        }

        if (entry.ProductType == ProductType.ShortSell)
        {
            return new(StopLossMethodDisposition.BrokerStopOrder, StopLossMethodResolutionReason.ShortSellEntry);
        }

        return method switch
        {
            StopLossExecutionMethod.NoProtectiveStop =>
                new(StopLossMethodDisposition.ProtectiveStopWaived, StopLossMethodResolutionReason.AsSelected),
            StopLossExecutionMethod.SoftwareStop =>
                new(StopLossMethodDisposition.SoftwareStop, StopLossMethodResolutionReason.AsSelected),
            StopLossExecutionMethod.AlternativeBrokerOrderType =>
                new(StopLossMethodDisposition.AlternativeBrokerOrderType, StopLossMethodResolutionReason.AsSelected),
            _ => new(StopLossMethodDisposition.NotImplementedFallbackToBrokerStop, StopLossMethodResolutionReason.UnknownMethod),
        };
    }

    /// <summary>
    /// FR-10, FR-06, #1002, IADR-0429 決定2: 解決結果が<b>実際に適用する手法</b>。発注しない（<see cref="StopLossMethodDisposition.Refused"/>）
    /// なら null。未実装の手法の退避は S0 と同じ扱いなので S0 である。
    /// </summary>
    public static StopLossExecutionMethod? AppliedMethodOf(StopLossMethodDisposition disposition) => disposition switch
    {
        StopLossMethodDisposition.BrokerStopOrder => StopLossExecutionMethod.BrokerStopOrder,
        StopLossMethodDisposition.NotImplementedFallbackToBrokerStop => StopLossExecutionMethod.BrokerStopOrder,
        StopLossMethodDisposition.ProtectiveStopWaived => StopLossExecutionMethod.NoProtectiveStop,
        StopLossMethodDisposition.SoftwareStop => StopLossExecutionMethod.SoftwareStop,
        StopLossMethodDisposition.AlternativeBrokerOrderType => StopLossExecutionMethod.AlternativeBrokerOrderType,
        StopLossMethodDisposition.Refused => null,
        _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "未知の解決結果"),
    };

    /// <summary>
    /// FR-10, FR-06, FR-11, #1002, IADR-0429 決定1: 解決結果を監査台帳・報告書へ渡す事実（<see cref="StopLossMethodResolved"/>）にする。
    /// </summary>
    public static StopLossMethodResolved ToEvent(
        OrderApproved approved, StopLossMethodResolution resolution, BrokerProvider provider, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(approved);
        ArgumentNullException.ThrowIfNull(resolution);

        return new StopLossMethodResolved(
            approved.DecisionId,
            approved.Intent.Symbol,
            approved.Intent.Market,
            approved.Intent.ProductType,
            approved.StopLossMethod,
            AppliedMethodOf(resolution.Disposition),
            resolution.Reason,
            provider,
            occurredAt);
    }
}

/// <summary>#1002, IADR-0429 決定2: 解決結果と、承認の手法と違う扱いになった理由（一致なら AsSelected）。</summary>
public sealed record StopLossMethodResolution(StopLossMethodDisposition Disposition, StopLossMethodResolutionReason Reason);

/// <summary>#819, IADR-0342 決定4: 手法の解決結果。</summary>
public enum StopLossMethodDisposition
{
    /// <summary>S0: 保護逆指値を同時発注し、未受理なら建玉を持たない（IADR-0210）。</summary>
    BrokerStopOrder,

    /// <summary>S0 以外が SIMULATE 以外の発注先で届いた。発注しない（見送り・Error ログ）。</summary>
    Refused,

    /// <summary>S2: 保護逆指値を発注せず建玉を保持し、免除の事実を発行する。</summary>
    ProtectiveStopWaived,

    /// <summary>未知: 未実装のため S0 と同じ扱い（警告ログ）。</summary>
    NotImplementedFallbackToBrokerStop,

    /// <summary>
    /// S1（#820, IADR-0344）: ブローカーへ保護レグを出さず、発注執行がソフトウェア逆指値を永続化する。
    /// 損切りライン到達（<c>StopLossTriggered</c>）で成行決済する。
    /// </summary>
    SoftwareStop,

    /// <summary>
    /// S3: 保護レグを代替のブローカー側注文種別（StopLimit / TrailingStop）で発注し、種別と拒否理由を監査へ残す
    /// （#821・IADR-0347）。<b>結果の扱いは S0 と同じ</b>（受理＝保護レグ／拒否＝建玉を持たない）。
    /// </summary>
    AlternativeBrokerOrderType,
}
