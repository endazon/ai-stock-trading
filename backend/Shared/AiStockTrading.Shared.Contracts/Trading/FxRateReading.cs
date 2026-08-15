namespace AiStockTrading.Shared.Contracts.Trading;

// FR-10, FR-11, #506, ADR-0022 決定5, IADR-0197: 為替レートの観測と、その**鮮度の判定結果**。
//
// 🔴 **「レートが無い」と「古いレートがある」を呼び出し側が区別できるようにするための型である。**
// 従来は鮮度切れを `null`（レート無し）へ潰しており、**入口（新規建て）と出口（手仕舞い）が
// 同じゲートで塞がれていた** —— 計画 ADR-0022 決定5 は「30 日超は**新規建てを停止する。
// 手仕舞い・損切りは止めない**」と両者を分けている。
public enum FxRateFreshness
{
    /// <summary>
    /// <b>鮮度を判定していない。</b> 生のレート源（日銀・FRED）が返す既定値である。
    /// <para>
    /// 🔴 <b><see cref="Fresh"/> と混同しない。</b> 判定していないのであって、
    /// 新鮮だと確かめたわけではない。ここを <c>Fresh</c> にすると、鮮度判定の装飾
    /// （<c>CachingFxRateSource</c>）を外した構成で<b>判定が消えたことに誰も気づかない</b>。
    /// </para>
    /// </summary>
    Unknown = 0,

    /// <summary>警告しきい値以下（既定 5 日以下）。通常運用。</summary>
    Fresh,

    /// <summary>
    /// 警告しきい値超〜絶対上限以下（既定 5 日超〜30 日以下）。
    /// <b>直近レートで続行し、新規建ても止めない</b>（ADR-0022 決定5）。気づくために警告だけ出す。
    /// </summary>
    Warning,

    /// <summary>
    /// 絶対上限超（既定 30 日超）。<b>新規建てを停止する。手仕舞い・損切りは止めない。</b>
    /// </summary>
    Expired,
}

/// <summary>
/// 取得できたレートと、その鮮度の判定結果。
/// </summary>
/// <param name="Rate">観測されたレート。<b>鮮度切れでも値そのものは失わせない</b>（IADR-0197 決定1）。</param>
/// <param name="Freshness">鮮度の判定結果。判定していない場合は <see cref="FxRateFreshness.Unknown"/>。</param>
public sealed record FxRateReading(FxRate Rate, FxRateFreshness Freshness)
{
    /// <summary>
    /// <b>新規建てに使ってよいか。</b> <see cref="FxRateFreshness.Expired"/> のときだけ false。
    /// <para>
    /// <see cref="FxRateFreshness.Unknown"/> を true 側に含めるのは、鮮度判定の装飾が無い構成
    /// （＝判定する主体が居ない）で従来どおり取引できるようにするためである。
    /// <b>判定を持つ構成では必ず装飾が最外に居る</b>（<c>FxRateSourceFactory</c> が保証する）。
    /// </para>
    /// </summary>
    public bool UsableForEntry => Freshness != FxRateFreshness.Expired;
}
