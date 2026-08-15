using AiStockTrading.Shared.Contracts.Events;

namespace AiStockTrading.Report.Domain;

/// <summary>
/// 期間内の為替情報源の状態。
/// </summary>
/// <param name="FellBacks">フォールバックへ切り替わった事象。</param>
/// <param name="Restorations">第一の情報源へ復帰した事象（<b>期間を持つ</b>）。</param>
/// <param name="StaleWarnings">
/// 鮮度警告（5 日超）。<b>停止域（30 日超）も同じ列に入る</b>——
/// 読み分けは <c>EntryBlocked</c> で行う（#381 停止側・IADR-0198 決定1）。
/// </param>
/// <param name="StaleCloses">
/// 🔴 <b>鮮度切れのレートで決済した取引</b>（#381 停止側・IADR-0198 決定3）。
/// <para>
/// <b><see cref="StaleWarnings"/> とは別の事実である。</b> あちらは「レート源の状態」、
/// こちらは「<b>その値で実際に取引した</b>」。よって<b>1 件ずつ載る</b>（抑止されていない）。
/// </para>
/// </param>
/// <param name="PrimarySourceCredits">
/// 期間内に実際に使った情報源が要求するクレジット表記（ADR-0022 決定1）。
/// <b>使っていない情報源のクレジットを載せない</b>——FRED フォールバック中に日銀のクレジットを
/// 出すのは事実に反する（IADR-0196 決定4）。
/// </param>
public sealed record FxSourceStatus(
    IReadOnlyList<FxRateSourceFellBack> FellBacks,
    IReadOnlyList<FxRateSourcePrimaryRestored> Restorations,
    IReadOnlyList<FxRateStale> StaleWarnings,
    IReadOnlyList<string> PrimarySourceCredits,
    IReadOnlyList<PositionClosedWithStaleFxRate> StaleCloses)
{
    /// <summary>
    /// 期間内に劣化を示す事象が 1 件も無かったか。
    /// <para>
    /// 🔴 <b><see cref="Restorations"/> も見る。</b> 期間より前から続いていたフォールバックが期間内に
    /// 復帰した場合、<see cref="FellBacks"/> は期間外にあり空になる——
    /// <b>復帰の明細行と「劣化はありませんでした」が並んで出る</b>ことになり、報告書が自己矛盾する
    /// （AI レビューの指摘・2026-08-15）。
    /// </para>
    /// </summary>
    /// <para>
    /// 🔴 <b><see cref="StaleCloses"/> も見る</b>（#381 停止側）。決済は鮮度警告の抑止と無関係に
    /// 1 件ずつ載るため、<b>警告が当日ぶん既に出ていて空でも決済だけが残る</b>ことがある——
    /// ここを落とすと<b>「劣化はありませんでした」と決済の明細が並ぶ</b>。復帰で踏んだ穴と同じ形である。
    /// </para>
    public bool IsClean =>
        FellBacks.Count == 0 && StaleWarnings.Count == 0 && Restorations.Count == 0 && StaleCloses.Count == 0;
}
