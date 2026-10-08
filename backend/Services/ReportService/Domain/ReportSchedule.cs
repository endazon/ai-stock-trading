using AiStockTrading.Shared.Contracts.Trading;

namespace ReportService.Domain;

// FR-06, UC-03〜05, 04_workflows/03_reporting-cycle, IADR-0115, #280: 自動生成の期間判定（純関数・決定的・副作用なし）。
//
// 計画書（fixed）の生成タイミング「日報＝毎営業日の閉場後 / 週報＝週末最終営業日の閉場後 / 月報＝月末最終営業日の閉場後」
// と「休場日は日報を生成しない。週報・月報は最終営業日基準で生成する」をそのまま写す。
//
// 時刻境界は JST（UTC+9）固定である。**生成の境界（いつ作るか）と集計の窓（どのセッションを数えるか）は別物**であり、
// 後者は SessionWindowOf が「生成境界までに大引けを迎えたセッション」として市場ごとの現地取引日へ写す
// （#1172・IADR-0492。IADR-0246 決定 4 の「集計境界は PeriodFillQuery 側で市場別になる」を改める——同じ日付で
// 引くと米国のセッションはどの日報にも載らなかった）。
//
// 「その瞬間に発火する」設計にはしない。境界を過ぎていて未生成なら対象、という判定にすることで、巡回間隔の粗さ・
// 遅延・プロセス再起動をすべて同じ機構で吸収する（cron 的な時刻一致を要求しない・IADR-0115 決定2）。
public static class ReportSchedule
{
    /// <summary>報告書の生成境界に用いる時差（JST・UTC+9）。PortfolioProjection.TradingDayOffset と同値。</summary>
    public static readonly TimeSpan JstOffset = TimeSpan.FromHours(9);

    // 日報の遡り上限（暦日）。連休を跨いで直近営業日へ届く程度に留め、長期停止からの一斉生成は行わない。
    private const int DailyLookbackDays = 7;

    // #1172, IADR-0492 決定 2: セッションの窓の始端（前の営業日）を探す遡り上限（暦日）。年末年始の連休を十分に跨ぐ。
    private const int SessionLookbackDays = 31;

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

    /// <summary>
    /// FR-06, 計画 ADR-0052 決定 2, #1156, IADR-0491 決定 4: 既存の報告書（種別と開始日）の集計期間を、自動生成（<see cref="Due"/>）と
    /// <b>同じ規則</b>で求める（作り直しが自動生成と同じ範囲の入力を引くため）。週報・月報の終端は区間の最終営業日、
    /// 区間に営業日が 1 日も無ければ区間の末日（自動生成はその期間を作らないが、手で作られた行は在り得る）。
    /// </summary>
    public static DueReport PeriodOf(ReportKind kind, DateOnly periodStart, ReportScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var end = kind switch
        {
            ReportKind.Daily => periodStart,
            ReportKind.Weekly => LastBusinessDay(periodStart, periodStart.AddDays(6), options) ?? periodStart.AddDays(6),
            _ => LastBusinessDay(periodStart, periodStart.AddMonths(1).AddDays(-1), options) ?? periodStart.AddMonths(1).AddDays(-1),
        };
        return Build(kind, periodStart, end);
    }

    /// <summary>
    /// FR-06, #1172, IADR-0492 決定 1・2: 報告書が集計するセッションの窓。<b>前の営業日の日報の生成境界の後〜期間の最終営業日の
    /// 日報の生成境界まで</b>に大引けを迎えたセッションを数える（境界はいずれも <see cref="ReportScheduleOptions.DailyAt"/>・JST）。
    /// 自動生成（<see cref="Due"/>）と作り直し（<see cref="PeriodOf"/>）の両方がこの 1 つの規則で約定・取り込みを絞る。
    /// <para>
    /// 週報・月報も日報と同じ境界で切る（その期間の日報の窓の和に等しくなり、日報の合計と週報・月報が食い違わない）。
    /// 営業日でない日の日報（手で作られた行）の窓は空になる——その日に閉場したセッションは次の営業日の日報が数える。
    /// </para>
    /// </summary>
    public static ReportSessionWindow SessionWindowOf(DueReport due, ReportScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(due);
        ArgumentNullException.ThrowIfNull(options);

        return new ReportSessionWindow(
            DailyBoundaryOnOrBefore(due.PeriodStart.AddDays(-1), options),
            DailyBoundaryOnOrBefore(due.PeriodEnd, options));
    }

    /// <summary>
    /// FR-06, 計画 ADR-0053 決定 3, #1172, IADR-0492 決定 6: 報告書の冒頭に書く「集計したセッション」の範囲（市場ごとの現地取引日）。
    /// <see cref="SessionWindowOf"/> の窓に大引けが入る現地の日のうち、両端の<b>セッションの無い日</b>（米国＝現地の土日・
    /// 東証＝構成された営業日でない日）を落とした [最初, 最後] を返す。並びは米国 → 東証（米国株が主）で、
    /// <paramref name="markets"/> に含まれる市場だけを返す。
    /// <para>
    /// 🔴 米国の祝日は本サービスが暦を持たないため落とさない（端に来ると、セッションの無い日が端に出る）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<ReportSessionRange> SessionRangesOf(
        DueReport due, ReportScheduleOptions options, IReadOnlyCollection<Market> markets)
    {
        ArgumentNullException.ThrowIfNull(markets);

        var window = SessionWindowOf(due, options);
        return [.. SessionRangeOrder.Where(markets.Contains).Select(market => SessionRangeOf(window, market, options))];
    }

    // 「集計したセッション」の並び（米国株が主のため米国を先に書く）。
    private static readonly Market[] SessionRangeOrder = [Market.UnitedStates, Market.Japan];

    private static ReportSessionRange SessionRangeOf(ReportSessionWindow window, Market market, ReportScheduleOptions options)
    {
        var (from, to) = window.TradingDays(market);
        while (from <= to && !IsSessionDay(market, from, options))
            from = from.AddDays(1);
        while (to >= from && !IsSessionDay(market, to, options))
            to = to.AddDays(-1);

        return new ReportSessionRange(market, from, to);
    }

    // 東証の休場日は構成された休場日（JST の営業日の暦）で読み、米国は現地の土日だけを落とす。
    private static bool IsSessionDay(Market market, DateOnly day, ReportScheduleOptions options) =>
        market == Market.Japan
            ? IsBusinessDay(day, options)
            : day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    /// <summary>
    /// FR-06, #1224, IADR-0516 決定 4: 瞬間 <paramref name="reportableAt"/> を窓に含む<b>日報の日付</b>（JST）。
    /// 日報 D の窓は (前の営業日の DailyAt, D の DailyAt] なので、DailyAt が瞬間以後になる最初の営業日である。
    /// 遡り・先送りは <see cref="SessionLookbackDays"/> で打ち切る（全日休場という構成の誤りで無限に進めない）。
    /// </summary>
    public static DateOnly DailyReportDayOf(DateTimeOffset reportableAt, ReportScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var jst = reportableAt.ToOffset(JstOffset);
        var day = DateOnly.FromDateTime(jst.DateTime);
        if (new DateTimeOffset(day.ToDateTime(options.DailyAt), JstOffset) < reportableAt)
            day = day.AddDays(1);
        for (var ahead = 0; ahead < SessionLookbackDays && !IsBusinessDay(day, options); ahead++)
            day = day.AddDays(1);

        return day;
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

    // #1172, IADR-0492 決定 2: date 以前で最後の営業日の日報の生成境界（JST の DailyAt）。遡りは SessionLookbackDays で打ち切り、
    // 見つからなければ打ち切った日の境界（全日休場という構成の誤りで、照会の範囲を無限に広げない）。
    private static DateTimeOffset DailyBoundaryOnOrBefore(DateOnly date, ReportScheduleOptions options)
    {
        var day = date;
        for (var back = 0; back < SessionLookbackDays && !IsBusinessDay(day, options); back++)
            day = day.AddDays(-1);

        return new DateTimeOffset(day.ToDateTime(options.DailyAt), JstOffset);
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
    /// <summary>
    /// 日報の生成境界（JST）。既定 16:00。報告書が集計するセッションの窓の境界も兼ねる（この時刻までに大引けを迎えた
    /// セッションをその日の日報が数える。#1172・IADR-0492 決定 2）。
    /// </summary>
    public TimeOnly DailyAt { get; init; } = new(16, 0);

    /// <summary>週報の生成境界（JST・当週最終営業日）。既定 16:30。</summary>
    public TimeOnly WeeklyAt { get; init; } = new(16, 30);

    /// <summary>月報の生成境界（JST・当月最終営業日）。既定 17:00。</summary>
    public TimeOnly MonthlyAt { get; init; } = new(17, 0);

    /// <summary>休場日（週末以外）。既定は空＝週末のみ非営業日（TradeDecision の MarketCalendar・IADR-0023 と同型）。</summary>
    public IReadOnlySet<DateOnly> Holidays { get; init; } = new HashSet<DateOnly>();
}

// FR-06, IADR-0115: 生成対象の期間。PeriodStart は TradingReport.PeriodStart（日報=当日 / 週報=週初 / 月報=月初）、
// PeriodEnd は集計対象の終端（＝当期の最終営業日）。約定・手動売買の取り込みは [PeriodStart, PeriodEnd] を市場の取引日として
// そのまま引かず、ReportSchedule.SessionWindowOf の窓で絞る（#1172・IADR-0492）。
public sealed record DueReport(ReportKind Kind, string PeriodKey, DateOnly PeriodStart, DateOnly PeriodEnd);
