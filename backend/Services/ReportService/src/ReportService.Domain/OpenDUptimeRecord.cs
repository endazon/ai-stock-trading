namespace AiStockTrading.Report.Domain;

// FR-06, FR-20, #338, INDEX 決定34, 04_report-templates 日報 §1 / 月報 §6.2, 06_daytrading-review §4.1・§4.2:
// OpenD の稼働率。**日報には当日の稼働率と Stage 1 日数への算入可否**、**月報には分布**を載せる。
//
// 計画の明文（決定34）: 「その日の通常取引時間の 50% 以上が稼働していれば 1 日として数え、50% 未満は算入しない。
// 分母は**その日の実際の通常取引時間**（通常日 6.5 時間／半日取引日 3.5 時間）、判定の基準時刻は**米国東部時間**。」
//
// 🔴 **分母を本型が発明しない。** 稼働分数の積み上げと分母の決定はリスク管理サービス（Stage1SessionUptime）が
// 権威源であり、本型は**その結果として得られた比率**だけを受け取る。ここで再計算すると、
// 分母の解釈が 2 箇所に分かれて必ず食い違う。
//
// **`null`（本型そのものが無い）＝照会できていない。** 稼働率を 0% と書くと「終日停止していた」と読める。

/// <summary>ある取引日の稼働率。</summary>
/// <param name="SessionDateEasternTime">米国東部時間での取引日（判定の基準時刻）。</param>
/// <param name="UptimeRatio">その日の通常取引時間に対する稼働の比率（0.0〜1.0）。</param>
public sealed record OpenDUptimeDay(DateOnly SessionDateEasternTime, decimal UptimeRatio);

/// <summary>当期間の稼働率の記録。</summary>
/// <param name="Days">取引日ごとの稼働率。空＝当期間に観測された取引日が無かった。</param>
/// <param name="Stage1CumulativeCountedDays">
/// Stage 1 の累計算入日数（期間内ではなく<b>累計</b>）。権威源が供給しないなら <c>null</c>（0 と書かない）。
/// </param>
public sealed record OpenDUptimeRecord(IReadOnlyList<OpenDUptimeDay> Days, int? Stage1CumulativeCountedDays = null);

/// <summary>月報 §6.2 の分布。</summary>
/// <param name="FullDays">稼働率 100% の日数。</param>
/// <param name="PartialCountedDays">50〜99%（Stage 1 の日数に算入する）の日数。</param>
/// <param name="NotCountedDays">50% 未満（算入しない）の日数。</param>
public sealed record OpenDUptimeDistribution(int FullDays, int PartialCountedDays, int NotCountedDays)
{
    /// <summary>Stage 1 の日数に算入される日数（100% ＋ 50〜99%）。</summary>
    public int CountedDays => FullDays + PartialCountedDays;
}

// FR-20, #338, INDEX 決定34, IADR-0251: 稼働率の集計（純関数・決定的）。
public static class OpenDUptimeAggregator
{
    /// <summary>Stage 1 の日数へ算入する下限（06_daytrading-review §4.2「50% 以上」）。</summary>
    public const decimal Stage1CountingThreshold = 0.50m;

    /// <summary>Stage 1 の目標算入日数（60 営業日）。</summary>
    public const int Stage1TargetDays = 60;

    /// <summary>その日の稼働率が Stage 1 の日数へ算入されるか（<b>50% ちょうどは算入する</b>＝「以上」）。</summary>
    public static bool IsCounted(decimal uptimeRatio) => uptimeRatio >= Stage1CountingThreshold;

    /// <summary>月報 §6.2 の分布を作る。</summary>
    public static OpenDUptimeDistribution Distribution(OpenDUptimeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var full = 0;
        var partial = 0;
        var notCounted = 0;

        foreach (var day in record.Days)
        {
            if (day.UptimeRatio >= 1m)
                full++;
            else if (IsCounted(day.UptimeRatio))
                partial++;
            else
                notCounted++;
        }

        return new OpenDUptimeDistribution(full, partial, notCounted);
    }
}
