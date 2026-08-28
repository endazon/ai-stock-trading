using AiStockTrading.Shared.Contracts.Events;

namespace ReportService.Domain;

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
/// <param name="Usages">
/// 🔴 <b>どの情報源を使ったかの記録</b>（暦日ごと・通貨ごと・源ごとに 1 件。#513・IADR-0225）。
/// <para>
/// <b>これが「静かな期間」の出典の根拠である。</b> 切替・復帰は遷移でしか残らないため
/// （IADR-0196 決定1）、平常時はこの列だけが「実際に使った源」を示す。
/// </para>
/// <para>
/// 🔴 <b>劣化ではない。</b> <see cref="IsClean"/> に入れてはならない——入れると
/// <b>平常運転の日が「劣化あり」と読める日報になる</b>。
/// </para>
/// </param>
public sealed record FxSourceStatus(
    IReadOnlyList<FxRateSourceFellBack> FellBacks,
    IReadOnlyList<FxRateSourcePrimaryRestored> Restorations,
    IReadOnlyList<FxRateStale> StaleWarnings,
    IReadOnlyList<string> PrimarySourceCredits,
    IReadOnlyList<PositionClosedWithStaleFxRate> StaleCloses,
    IReadOnlyList<FxRateSourceUsed> Usages)
{
    /// <summary>
    /// 期間内に<b>使ったと台帳が示す情報源</b>の名前（重複なし）。使用記録と遷移の両方から引く——
    /// 遷移イベントも <c>SourceName</c> を運ぶ＝使用の証拠だからである（#513・IADR-0225 決定E）。
    /// </summary>
    public IReadOnlyList<string> UsedSourceNames =>
        [.. Usages.Select(e => e.SourceName)
            .Concat(FellBacks.Select(e => e.SourceName))
            .Concat(Restorations.Select(e => e.SourceName))
            .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// 期間内に<b>第一の情報源から取得した証拠</b>のある源の名前（重複なし）。
    /// <b>復帰イベントも第一の源を使った証拠である</b>（復帰＝第一へ戻して使った）。
    /// </summary>
    public IReadOnlyList<string> PrimarySourceNames =>
        [.. Usages.Where(e => e.IsPrimary).Select(e => e.SourceName)
            .Concat(Restorations.Select(e => e.SourceName))
            .Distinct(StringComparer.Ordinal)];

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
