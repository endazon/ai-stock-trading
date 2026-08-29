using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Domain;

// FR-06, FR-20, #569, INDEX 決定34, 04_report-templates 日報 §1 / 月報 §6.2, IADR-0271:
// **報告書へ供給する OpenD 稼働率**を、稼働の観測ログ（Stage1SessionUptime）から取り出す純関数。
//
// 🔴 **稼働率の権威源はここだけである。** 報告書サービス側の稼働率レコード（`OpenDUptimeRecord`）は
// 「分母を本型が発明しない。稼働分数の積み上げと分母の決定はリスク管理サービスが権威源であり、
// 本型はその結果として得られた比率だけを受け取る」と明記している。本クラスがその受け渡し点である。
//
// 🔴 **内蔵 paper の観測は OpenD 稼働率ではない。** 内蔵 paper は「外部へ一度も発注しない」
// （BrokerProvider.InternalPaper の明文）ため OpenD を経由しない。その稼働分数を OpenD 稼働率として
// 報告すると、**OpenD が落ちていた日を「稼働していた」と描く**ことになる。
public static class OpenDUptimeReporting
{
    /// <summary>
    /// その発注先が <b>OpenD を経由する</b>か（＝その稼働観測が OpenD 稼働率の母集団に入るか）。
    /// <para>
    /// **許可制である**（拒否リストではない。<see cref="Stage1DayQualification.CountedProvider"/> と同じ規律）。
    /// 将来増える発注先が既定で OpenD 稼働率へ流れ込まないようにする。
    /// </para>
    /// </summary>
    public static bool IsOpenDBacked(BrokerProvider provider) =>
        provider is BrokerProvider.MoomooSimulate or BrokerProvider.MoomooReal;

    /// <summary>
    /// 取引日ごとの稼働率（昇順）。同じ取引日に複数の発注先の観測があるときは<b>最大値</b>を採る
    /// （どれか 1 つの経路で OpenD が上がっていれば、その日 OpenD は上がっていた）。
    /// <para>
    /// **OpenD を経由しない発注先しか観測が無い日は、結果に現れない。**
    /// 🔴 <b>0% の日として並べない</b>——「終日停止していた」という別の事実になる
    /// （未供給と 0 を潰さない規律）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<OpenDSessionUptimeDay> Days(IEnumerable<Stage1SessionUptime> uptimes)
    {
        ArgumentNullException.ThrowIfNull(uptimes);

        return
        [
            .. uptimes
                .Where(u => IsOpenDBacked(u.Provider))
                .GroupBy(u => u.SessionDateEasternTime)
                .Select(g => new OpenDSessionUptimeDay(g.Key, g.Max(Stage1SessionHypotheses.UptimeRatio)))
                .OrderBy(d => d.SessionDateEasternTime),
        ];
    }
}

/// <summary>
/// FR-06, FR-20, #569: ある取引日の OpenD 稼働率（報告書への供給単位）。
/// </summary>
/// <param name="SessionDateEasternTime">米国東部時間での取引日（§4.2 の判定基準時刻）。</param>
/// <param name="UptimeRatio">その日の通常取引時間に対する稼働の比率（0.0〜1.0・仮説の最小値）。</param>
public sealed record OpenDSessionUptimeDay(DateOnly SessionDateEasternTime, decimal UptimeRatio);
