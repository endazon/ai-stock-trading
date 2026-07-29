namespace AiStockTrading.Report.Domain;

// FR-06, UC-03〜05, 04_workflows/03_reporting-cycle, IADR-0115, #280: 自動生成の期間判定（純関数・決定的・副作用なし）。
//
// 計画書（fixed）の生成タイミング「日報＝毎営業日の閉場後 / 週報＝週末最終営業日の閉場後 / 月報＝月末最終営業日の閉場後」
// と「休場日は日報を生成しない。週報・月報は最終営業日基準で生成する」をそのまま写す。
//
// 時刻境界は JST（UTC+9）固定で、PortfolioProjection.TradingDayOffset（=+9h）と整合させる。市場別の取引日境界の
// 解釈は #249 の管轄であり、ここでは扱わない。
//
// 「その瞬間に発火する」設計にはしない。境界を過ぎていて未生成なら対象、という判定にすることで、巡回間隔の粗さ・
// 遅延・プロセス再起動をすべて同じ機構で吸収する（cron 的な時刻一致を要求しない・IADR-0115 決定2）。
public static class ReportSchedule
{
    /// <summary>報告書の生成境界に用いる時差（JST・UTC+9）。PortfolioProjection.TradingDayOffset と同値。</summary>
    public static readonly TimeSpan JstOffset = TimeSpan.FromHours(9);

    // 日報の遡り上限（暦日）。連休を跨いで直近営業日へ届く程度に留め、長期停止からの一斉生成は行わない。
    private const int DailyLookbackDays = 7;

    /// <summary>
    /// 指定時刻において「生成境界を過ぎている期間」を種別ごとに 0〜1 件ずつ返す。既に生成済みかどうかは見ない
    /// （冪等の判定は呼び出し側が PeriodKey の存在で行う・IADR-0115 決定3）。
    /// </summary>
    public static IReadOnlyList<DueReport> Due(DateTimeOffset instant, ReportScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var nowJst = instant.ToOffset(JstOffset);
        var today = DateOnly.FromDateTime(nowJst.DateTime);
        var timeOfDay = TimeOnly.FromDateTime(nowJst.DateTime);

        var due = new List<DueReport>(3);

        if (DailyTarget(today, timeOfDay, options) is { } daily)
            due.Add(Build(ReportKind.Daily, daily, daily));

        // 週報: 当 ISO 週（月曜〜日曜）の最終営業日。today は必ず当週内にあるため、境界超過の判定は
        // 「最終営業日より後の日にいる」または「最終営業日当日で境界時刻に達している」。
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        if (LastBusinessDay(weekStart, weekStart.AddDays(6), options) is { } weekEnd
            && HasPassed(today, timeOfDay, weekEnd, options.WeeklyAt))
        {
            due.Add(Build(ReportKind.Weekly, weekStart, weekEnd));
        }

        // 月報: 当月の最終営業日。
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        if (LastBusinessDay(monthStart, monthStart.AddMonths(1).AddDays(-1), options) is { } monthEnd
            && HasPassed(today, timeOfDay, monthEnd, options.MonthlyAt))
        {
            due.Add(Build(ReportKind.Monthly, monthStart, monthEnd));
        }

        return due;
    }

    /// <summary>営業日か（土日でも構成された休場日でもない）。</summary>
    public static bool IsBusinessDay(DateOnly date, ReportScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && !options.Holidays.Contains(date);
    }

    // 日報の対象＝「閉場境界を過ぎた直近の営業日」。当日が営業日でも境界時刻前なら前営業日へ下がる。
    // 前営業日まで遡るのは、確定した日報が翌営業日の取引方針になるため（夜間・早朝の再起動で取りこぼさない）。
    private static DateOnly? DailyTarget(DateOnly today, TimeOnly timeOfDay, ReportScheduleOptions options)
    {
        for (var back = 0; back <= DailyLookbackDays; back++)
        {
            var candidate = today.AddDays(-back);
            if (!IsBusinessDay(candidate, options))
                continue;

            // 当日は閉場境界に達して初めて対象になる（達していなければさらに前の営業日を探す）。
            if (back == 0 && timeOfDay < options.DailyAt)
                continue;

            return candidate;
        }

        return null;
    }

    // 区間 [from, to] のうち最後の営業日。1 日も無ければ null（全日休場の週・月）。
    private static DateOnly? LastBusinessDay(DateOnly from, DateOnly to, ReportScheduleOptions options)
    {
        for (var candidate = to; candidate >= from; candidate = candidate.AddDays(-1))
        {
            if (IsBusinessDay(candidate, options))
                return candidate;
        }

        return null;
    }

    // 境界（boundaryDay の boundaryTime）を過ぎているか。日付が進んでいれば時刻は問わない。
    private static bool HasPassed(DateOnly today, TimeOnly timeOfDay, DateOnly boundaryDay, TimeOnly boundaryTime) =>
        today > boundaryDay || (today == boundaryDay && timeOfDay >= boundaryTime);

    // PeriodKey は種別ごとの自然キー（ReportPeriod）に委ねる。週報/月報は期間開始日から導出すると
    // ISO 週・年月の表記が期間と一致する（ReportDraftService の PeriodKey 検証とも整合する）。
    private static DueReport Build(ReportKind kind, DateOnly periodStart, DateOnly periodEnd) =>
        new(kind, ReportPeriod.ExpectedKey(kind, periodStart), periodStart, periodEnd);
}

// FR-06, IADR-0115: 生成境界の構成（JST の壁時計時刻・休場日集合）。既定は東証の大引け 15:30 の後に置く。
public sealed record ReportScheduleOptions
{
    /// <summary>日報の生成境界（JST）。既定 16:00。</summary>
    public TimeOnly DailyAt { get; init; } = new(16, 0);

    /// <summary>週報の生成境界（JST・当週最終営業日）。既定 16:30。</summary>
    public TimeOnly WeeklyAt { get; init; } = new(16, 30);

    /// <summary>月報の生成境界（JST・当月最終営業日）。既定 17:00。</summary>
    public TimeOnly MonthlyAt { get; init; } = new(17, 0);

    /// <summary>休場日（週末以外）。既定は空＝週末のみ非営業日（TradeDecision の MarketCalendar・IADR-0023 と同型）。</summary>
    public IReadOnlySet<DateOnly> Holidays { get; init; } = new HashSet<DateOnly>();
}

// FR-06, IADR-0115: 生成対象の期間。PeriodStart は TradingReport.PeriodStart（日報=当日 / 週報=週初 / 月報=月初）、
// PeriodEnd は集計対象の終端（＝当期の最終営業日）で、約定の照会範囲 [PeriodStart, PeriodEnd] に用いる。
public sealed record DueReport(ReportKind Kind, string PeriodKey, DateOnly PeriodStart, DateOnly PeriodEnd);
