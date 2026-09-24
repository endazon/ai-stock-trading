namespace AiStockTrading.Shared.Contracts.Observability;

/// <summary>
/// FR-10, NFR-07, #889, ADR-0041 決定2, IADR-0372: <b>統制上限の基準資金（equity）を読んだ結果。</b>
/// <para>
/// 🔴 <b>これは観測の語彙であって、門ではない。</b> どの値でも <c>ICapitalBaselineStore.GetCurrent()</c> が
/// 返す値は従来と同じである（#889 の裁定 —— 残高 0 で新規建てを止めるかどうか —— は未了であり、
/// 本列挙はその裁定に必要な事実を見えるようにするためだけにある）。
/// </para>
/// </summary>
public enum CapitalBaselineReadOutcome
{
    /// <summary>直前の取引日の観測を供給した（平常）。</summary>
    Supplied,

    /// <summary>
    /// 🔴 供給したが、<b>その行は直前の取引日のものではない</b>（観測に欠落がある）。
    /// <para>
    /// <b>#889 の症状そのものである。</b> 口座照会が残高 0 を返した日は供給側の門が未供給へ倒すため
    /// <b>その取引日の行が 1 行も書かれず</b>、読み出しは前取引日の正の値を鮮度（既定 4 日）が切れるまで
    /// 返し続ける ——<b>新規建ては止まらない</b>。
    /// </para>
    /// <para>
    /// 🔴 <b>原因（残高 0 / 照会障害 / プロセス停止）は区別しない。</b> 読み出し側から区別はできないし、
    /// 危険なのは原因ではなく<b>状態</b>（古い分母で統制が回っていること）である。
    /// 巡回は土日も回り取引日は米国東部時間の暦日で数えるため、期待される間隔は 1 日である（IADR-0354 決定2）。
    /// </para>
    /// </summary>
    SuppliedWithGap,

    /// <summary>当日より前の取引日の行が 1 行も無い（未供給。初回起動・配備直後）。</summary>
    UnavailableNoRow,

    /// <summary>鮮度が切れている（既定 4 日超。未供給）。</summary>
    UnavailableStale,

    /// <summary>
    /// 最新行の評価額が 0 以下（未供給。IADR-0354 決定7）。
    /// <para>
    /// 🔴 <b>供給側の 0 とは帰結が逆である。</b> ブローカーが 0 を返した日は行が書かれないので
    /// <see cref="SuppliedWithGap"/> になり<b>止まらない</b>が、人手で 0 を 1 行入れると
    /// ここで<b>止まる</b>（運用 Runbook が認めている経路）。**この非対称は現時点で意図した状態である**
    /// （#889 の裁定待ち）。
    /// </para>
    /// </summary>
    UnavailableNonPositive,
}
