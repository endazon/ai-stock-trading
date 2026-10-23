namespace AiStockTrading.Shared.Contracts.Observability;

/// <summary>
/// FR-10, NFR-07, #889, ADR-0041 決定2, IADR-0372: <b>統制上限の基準資金（equity）を読んだ結果。</b>
/// <para>
/// 🔴 <b>これは観測の語彙であって、門ではない。</b> 門は <c>ICapitalBaselineStore.GetCurrent()</c> にあり、
/// 本列挙はその帰結を数え分けるためにある。
/// </para>
/// <para>
/// 🔴 FR-10, #1423 M-3, ADR-0073, IADR-0581（2026-10-24）: 旧 <c>SuppliedWithGap</c>（値を返すが直前の取引日の観測が
/// 欠けている）は、計画の裁定（直前の米国東部暦日に成功した観測が無ければ新規建てを拒否する）で<b>未供給へ移った</b>ため
/// <see cref="UnavailableMissingPreviousDay"/> へ改名した。指標のラベル値も変わる（配備前のラベルで数えた値と混ぜない）。
/// </para>
/// </summary>
public enum CapitalBaselineReadOutcome
{
    /// <summary>直前の取引日の観測を供給した（平常）。</summary>
    Supplied,

    /// <summary>
    /// 🔴 <b>直前の米国東部暦日に成功した観測が無い</b>（未供給。新規建てを拒否する。ADR-0073 決定1・2）。
    /// <para>
    /// 口座照会が残高 0 を返した日・照会障害・プロセス停止のいずれでも、その暦日の行は 1 行も書かれない。
    /// 旧実装はここで前取引日の正の値を鮮度（既定 4 日）まで返し続けた（#889 の症状。旧名 <c>SuppliedWithGap</c>）。
    /// </para>
    /// <para>
    /// 🔴 <b>原因（残高 0 / 照会障害 / プロセス停止）は区別しない。</b> 読み出し側から区別はできないし、
    /// 危険なのは原因ではなく<b>状態</b>（古い分母で統制が回ること）である。
    /// 巡回は土日も回り取引日は米国東部時間の暦日で数えるため、期待される間隔は 1 日である（IADR-0354 決定2）。
    /// </para>
    /// </summary>
    UnavailableMissingPreviousDay,

    /// <summary>当日より前の取引日の行が 1 行も無い（未供給。初回起動・配備直後）。</summary>
    UnavailableNoRow,

    /// <summary>鮮度が切れている（既定 4 日超。未供給）。</summary>
    UnavailableStale,

    /// <summary>
    /// 最新行の評価額が 0 以下（未供給。IADR-0354 決定7）。
    /// <para>
    /// ブローカーが 0 を返した日は行が書かれないので、翌暦日から <see cref="UnavailableMissingPreviousDay"/> で止まる
    /// （#1423 M-3 で供給側の 0 と帰結が揃った。当日のうちに止めるかは別の裁定で、ADR-0073 の射程の外）。
    /// 人手で 0 を 1 行入れると、こちらで止まる（運用 Runbook が認めている経路）。
    /// </para>
    /// </summary>
    UnavailableNonPositive,
}
