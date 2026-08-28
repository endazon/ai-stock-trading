using System.Globalization;
using ReportService.Application.Services;
using ReportService.Domain;

namespace ReportService.Infrastructure.Polling;

// FR-06, UC-03〜05, IADR-0115 決定6, #280: 報告書自動生成の構成。既定は安全側（無効・opt-in）。
// 有効化しない限り常駐は登録されず、現行挙動とバイト等価（ObservedDrawdownRefreshOptions・IADR-0103 と同型）。
//
// 生成境界は JST の壁時計時刻で与える（PortfolioProjection.TradingDayOffset=+9 と整合）。不正な表記・非正値は
// すべて既定へ倒す（黙って生成が止まる・過度に早い時刻で閉場前に生成する、といった事故を避ける）。
public sealed class ReportAutoGenerationOptions
{
    public const string SectionName = "Reports:AutoGeneration";

    /// <summary>常駐スケジューラを起動するか。既定 false（opt-in・安全側）。</summary>
    public bool Enabled { get; set; }

    /// <summary>巡回間隔（秒）。既定 300（5 分）。境界超過＋未生成で判定するため厳密さは要らない。</summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>日報の生成境界（JST・HH:mm）。既定 16:00（東証の大引け 15:30 の後）。</summary>
    public string DailyAtJst { get; set; } = "16:00";

    /// <summary>週報の生成境界（JST・HH:mm・当週最終営業日）。既定 16:30。</summary>
    public string WeeklyAtJst { get; set; } = "16:30";

    /// <summary>月報の生成境界（JST・HH:mm・当月最終営業日）。既定 17:00。</summary>
    public string MonthlyAtJst { get; set; } = "17:00";

    /// <summary>休場日（yyyy-MM-dd）。既定は空＝週末のみ非営業日。解釈できない要素は無視する。</summary>
    public string[] Holidays { get; set; } = [];

    /// <summary>フロントマターの対象市場表記（"JP"/"US" 等）。既定は空。</summary>
    public string[] Markets { get; set; } = [];

    /// <summary>適用する全体前提条件のバージョン（FR-17）。非正値は 1 へ倒す。</summary>
    public int AssumptionsVersion { get; set; } = 1;

    /// <summary>
    /// FR-09, IADR-0116 決定2: 提示（確定依頼）の通知イベントを発行するか。既定 **true**。
    /// 本常駐そのものが既定無効（opt-in）であり、有効化した利用者にとって「報告書は作られているのに何も届かない」は
    /// 危険なため、二段目の opt-in にしない（既定挙動のバイト等価は <see cref="Enabled"/> の既定 false が担保する）。
    /// 実送信は通知サービス側の Discord 設定が入って初めて発火する（未設定なら no-op・IADR-0020/0062）。
    /// </summary>
    public bool NotifyOnDraftPresented { get; set; } = true;

    /// <summary>巡回間隔（非正値は既定 300 秒へ倒す＝暴走ループにしない）。</summary>
    public TimeSpan Interval => TimeSpan.FromSeconds(IntervalSeconds > 0 ? IntervalSeconds : 300);

    /// <summary>構成値を生成オーケストレータの設定へ写す。解釈できない値は既定へ倒す（fail-safe）。</summary>
    public ReportAutoGenerationSettings ToSettings()
    {
        var defaults = new ReportScheduleOptions();

        return new ReportAutoGenerationSettings
        {
            Schedule = new ReportScheduleOptions
            {
                DailyAt = ParseTime(DailyAtJst, defaults.DailyAt),
                WeeklyAt = ParseTime(WeeklyAtJst, defaults.WeeklyAt),
                MonthlyAt = ParseTime(MonthlyAtJst, defaults.MonthlyAt),
                Holidays = ParseHolidays(Holidays),
            },
            Markets = [.. Markets.Where(m => !string.IsNullOrWhiteSpace(m))],
            AssumptionsVersion = AssumptionsVersion > 0 ? AssumptionsVersion : 1,
        };
    }

    private static TimeOnly ParseTime(string? value, TimeOnly fallback) =>
        TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static IReadOnlySet<DateOnly> ParseHolidays(IEnumerable<string> values)
    {
        var days = new HashSet<DateOnly>();
        foreach (var value in values)
        {
            if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, out var day))
                days.Add(day);
        }

        return days;
    }
}
