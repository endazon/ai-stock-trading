using System.Globalization;
using System.Text.RegularExpressions;
using ReportService.Domain;

namespace ReportService.Features.Reports.ReingestKnowledgeBase;

// FR-08, #1028, IADR-0436 決定 1: 入れ直しの対象範囲（期間キーの範囲か全件）。純関数。
//
// 期間キーは種別ごとに書式が違う（ReportPeriod.ExpectedKey: daily-yyyy-MM-dd / weekly-yyyy-Www / monthly-yyyy-MM）ため、
// **文字列の大小では比べない**（"daily-…" < "monthly-…" < "weekly-…" になる）。キーを期間へ直し、
// `from` の期間の初日 ≦ PeriodStart ≦ `to` の期間の末日 の報告書を対象にする（種別を問わない）。
// 全件は `all: true` で明示させる（空の要求を全件と読まない）。
public sealed partial record ReportKnowledgeReingestScope(
    bool All,
    string? FromPeriodKey,
    string? ToPeriodKey,
    DateOnly? FromDate,
    DateOnly? ToDate)
{
    public const string AllLabel = "all";

    [GeneratedRegex(@"\A(?<kind>daily|weekly|monthly)-(?<rest>[0-9W-]{4,10})\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"\A(?<year>\d{4})-W(?<week>\d{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex WeekPattern();

    public bool Includes(TradingReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return All
            || ((FromDate is null || report.PeriodStart >= FromDate) && (ToDate is null || report.PeriodStart <= ToDate));
    }

    // 監査・応答に載せる範囲の表記（"all" / "daily-2026-07-01..monthly-2026-08" / 片側は "(先頭)" "(末尾)"）。
    public string Describe() => All ? AllLabel : $"{FromPeriodKey ?? "(先頭)"}..{ToPeriodKey ?? "(末尾)"}";

    // 要求を範囲へ直す。成功なら (scope, null)、不正なら (null, 理由)。
    public static (ReportKnowledgeReingestScope? Scope, string? Error) Parse(bool? all, string? fromPeriodKey, string? toPeriodKey)
    {
        var hasFrom = !string.IsNullOrWhiteSpace(fromPeriodKey);
        var hasTo = !string.IsNullOrWhiteSpace(toPeriodKey);

        if (all == true)
        {
            return hasFrom || hasTo
                ? (null, "all と期間キーの範囲は同時に指定できません。")
                : (new ReportKnowledgeReingestScope(true, null, null, null, null), null);
        }

        if (!hasFrom && !hasTo)
            return (null, "対象を指定してください（all: true か、fromPeriodKey / toPeriodKey の少なくとも一方）。");

        DateOnly? fromDate = null;
        DateOnly? toDate = null;
        if (hasFrom)
        {
            if (TryParsePeriod(fromPeriodKey!, out var start, out _) is false)
                return (null, $"fromPeriodKey の形式が不正です（daily-yyyy-MM-dd / weekly-yyyy-Www / monthly-yyyy-MM）。");
            fromDate = start;
        }

        if (hasTo)
        {
            if (TryParsePeriod(toPeriodKey!, out _, out var end) is false)
                return (null, $"toPeriodKey の形式が不正です（daily-yyyy-MM-dd / weekly-yyyy-Www / monthly-yyyy-MM）。");
            toDate = end;
        }

        if (fromDate is { } f && toDate is { } t && f > t)
            return (null, "fromPeriodKey の期間が toPeriodKey の期間より後です。");

        return (new ReportKnowledgeReingestScope(
            false, hasFrom ? fromPeriodKey : null, hasTo ? toPeriodKey : null, fromDate, toDate), null);
    }

    // 期間キー → 期間（初日・末日）。ReportPeriod.ExpectedKey の逆。
    public static bool TryParsePeriod(string periodKey, out DateOnly start, out DateOnly end)
    {
        start = default;
        end = default;

        var match = KeyPattern().Match(periodKey);
        if (!match.Success)
            return false;

        var rest = match.Groups["rest"].Value;
        switch (match.Groups["kind"].Value)
        {
            case "daily":
                if (!DateOnly.TryParseExact(rest, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    return false;
                start = end = day;
                return true;

            case "monthly":
                if (!DateOnly.TryParseExact(rest + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
                    return false;
                start = first;
                end = first.AddMonths(1).AddDays(-1);
                return true;

            case "weekly":
                var week = WeekPattern().Match(rest);
                if (!week.Success)
                    return false;
                var year = int.Parse(week.Groups["year"].Value, CultureInfo.InvariantCulture);
                var number = int.Parse(week.Groups["week"].Value, CultureInfo.InvariantCulture);
                if (year < 1 || number < 1 || number > ISOWeek.GetWeeksInYear(year))
                    return false;
                start = DateOnly.FromDateTime(ISOWeek.ToDateTime(year, number, DayOfWeek.Monday));
                end = start.AddDays(6);
                return true;

            default:
                return false;
        }
    }
}
