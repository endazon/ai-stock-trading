using System.Globalization;

namespace ReportService.Domain;

// FR-06, 04_report-templates, IADR-0032: 報告書の期間表記と自然キーの導出（純関数）。
// period（フロントマター）と PeriodKey の形式を種別ごとに一元化する（daily=yyyy-MM-dd / weekly=yyyy-Www(ISO週) / monthly=yyyy-MM）。
public static class ReportPeriod
{
    // フロントマター period の表記。
    public static string Label(ReportKind kind, DateOnly date) => kind switch
    {
        ReportKind.Weekly => $"{ISOWeek.GetYear(date.ToDateTime(TimeOnly.MinValue))}-W{ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue)):D2}",
        ReportKind.Monthly => date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        _ => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    // 自然キー（"daily-2026-07-10" / "weekly-2026-W28" / "monthly-2026-07"）。
    public static string ExpectedKey(ReportKind kind, DateOnly date) =>
        $"{kind.ToString().ToLowerInvariant()}-{Label(kind, date)}";
}
