using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06, FR-07, 計画 ADR-0059 決定 4, #1218, IADR-0519 決定 4: 週次目標の参照値を持つ週報を選ぶ。
// 「当週の週報」は**前週の週報**（当週を対象期間とする目標を持つ週報。週報 §6 は「翌週」の目標を持つ）である。
// 前週の週報が確定していなければ、当週より前を対象とする確定済み週報のうち最新を使い、注記する（03_reporting-cycle「上位方針の欠落」と同じ）。
// 確定済みが 1 件も無ければ「週次目標なし」。🔴 当週以後の週報（翌週の目標を持つ）は使わない——日報の上位方針（最新の確定済み週報）とは選び方が違う。
public static class WeeklyGoalReferenceResolver
{
    /// <summary>ISO 週 <paramref name="weekStart"/>（月曜）を対象とする週次目標の参照値を選び、方針から書式行を読む。</summary>
    public static WeeklyGoalReference Resolve(IReportStore store, DateOnly weekStart)
    {
        ArgumentNullException.ThrowIfNull(store);

        var expectedKey = ReportPeriod.ExpectedKey(ReportKind.Weekly, weekStart.AddDays(-7));
        var previous = store.Get(expectedKey);
        var source = previous is { Report.State: ReportState.Confirmed } ? previous.Report : LatestConfirmedBefore(store, weekStart);

        return source is null
            ? WeeklyGoalReference.None(expectedKey)
            : new WeeklyGoalReference(expectedKey, source.PeriodKey, WeeklyGoalLine.Parse(source.PolicySummary));
    }

    // 当週より前を対象とする確定済み週報のうち最新。通常は直近の確定済み（1 行の照会）で足り、それが当週以後のとき（過去の日報の作り直しなど）だけ一覧から選ぶ。
    private static TradingReport? LatestConfirmedBefore(IReportStore store, DateOnly weekStart)
    {
        var latest = store.GetLatestConfirmed(ReportKind.Weekly);
        if (latest is null)
            return null;
        if (latest.Report.PeriodStart < weekStart)
            return latest.Report;

        return store.List()
            .Where(r => r.Kind == ReportKind.Weekly && r.State == ReportState.Confirmed && r.PeriodStart < weekStart)
            .MaxBy(r => r.PeriodStart);
    }
}
