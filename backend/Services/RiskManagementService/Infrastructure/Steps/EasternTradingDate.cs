namespace RiskManagementService.Infrastructure.Steps;

// FR-20, #386, #385, 06_daytrading-review §4.2: 時刻（UTC）を**米国東部時間の取引日・分**へ写す。
//
// §4.2 は判定の基準時刻を米国東部時間と定め、その理由を「サマータイムにより日本時間での取引時間が
// 22:30〜翌 5:00 と 23:30〜翌 6:00 の間で変わる。日本時間の固定時刻で集計すると切替時に 1 時間分の誤差が出る」と
// 書いている。**変換は 1 か所（本クラス）に閉じる**——東部時間の解釈が実装ごとに割れると、
// 取引日の境界が指標ごとにずれる。変換方式は MarketCalendar（FR-02・IADR-0023）と同一にしてある。
//
// **判定そのもの（ドメイン）はタイムゾーン変換を持たない**（IADR-0137 決定1）。ここは供給側である。
public static class EasternTradingDate
{
    // クロスプラットフォームのため OS で TZ ID を切り替える（MarketCalendar・SystemClock と同方針）。
    private static readonly TimeZoneInfo UsEasternZone =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    public static DateOnly From(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, UsEasternZone).DateTime);

    /// <summary>
    /// FR-20, #385, §4.2: 米国東部時間での「取引日」と「その日の 0 時からの分数」。
    /// <para>
    /// DST の切替は <see cref="TimeZoneInfo"/> が吸収する——同じ現地時刻（例: 9:30 ET）は、EST でも EDT でも
    /// 同じ分（570）へ写る。UTC の固定時刻で数えるとここが 1 時間ずれ、稼働分数が切替日にだけ狂う。
    /// </para>
    /// </summary>
    public static (DateOnly Date, int MinuteOfDay) SessionMinuteOf(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, UsEasternZone);
        return (DateOnly.FromDateTime(local.DateTime), (local.Hour * 60) + local.Minute);
    }
}
