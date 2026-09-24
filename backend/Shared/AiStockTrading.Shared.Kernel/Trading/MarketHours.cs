using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Kernel.Trading;

// FR-02, FR-03, UC-01, UC-02, #337, #909, IADR-0023, IADR-0245, IADR-0380 決定1: 「今この瞬間、その市場は場中か」の
// **単一情報源**（純関数）。取引判断サービス（サイクル起動）と市場監視サービス（巡回・S1 の到達判定）が同じ答えを見る。
//
// 🔴 **2 つのサービスが「開場か」で食い違う状態を作らない。** 片方が開場と読んで発注し、もう片方が閉場と読んで
// 保護を止める組み合わせは、無保護の建玉をそのまま残す（#909 の事故はこの向きの一歩手前だった）。
//
// 🔴 **判定はすべて市場ローカル時刻で行う**（米国市場は米国東部時間）。サマータイムの切り替えで日本時間との差が
// 1 時間ずれるため、**固定のオフセットで換算しない**（計画 04_workflows/01 の明文）。DST の切替は TimeZoneInfo が
// 吸収する —— 同じ現地時刻（例: 9:30 ET）は EST でも EDT でも同じ場中判定へ写る。
//
// 休場日・半日取引日は MarketHolidays（規則計算）を基礎とし、臨時休場・臨時の半日は呼び出し側が構成から**足す**。
public static class MarketHours
{
    /// <summary>次の開場時刻を探す上限日数（連休・臨時休場を十分に跨ぐ。見つからなければ null を返す）。</summary>
    private const int NextOpenScanDays = 14;

    // クロスプラットフォームのため OS で TZ ID を切り替える（SystemClock・MarketCalendar と同方針）。
    private static readonly TimeZoneInfo JapanZone = Resolve("Tokyo Standard Time", "Asia/Tokyo");
    private static readonly TimeZoneInfo UsEasternZone = Resolve("Eastern Standard Time", "America/New_York");

    /// <summary>その市場の基準タイムゾーン。</summary>
    public static TimeZoneInfo ZoneOf(Market market) => market == Market.Japan ? JapanZone : UsEasternZone;

    /// <summary>
    /// その瞬間にその市場が場中か（週末・休場日でなく、かつ取引時間内）。
    /// </summary>
    /// <param name="market">市場。</param>
    /// <param name="instant">判定する瞬間（タイムゾーンは問わない）。</param>
    /// <param name="extraHolidays">規則計算に**足す**臨時休場日（市場ローカルの日付）。</param>
    /// <param name="extraHalfDays">規則計算に**足す**臨時の半日取引日（市場ローカルの日付）。</param>
    public static bool IsOpen(
        Market market,
        DateTimeOffset instant,
        IReadOnlySet<DateOnly>? extraHolidays = null,
        IReadOnlySet<DateOnly>? extraHalfDays = null)
    {
        var local = TimeZoneInfo.ConvertTime(instant, ZoneOf(market));
        var date = DateOnly.FromDateTime(local.DateTime);
        if (!IsTradingDay(market, date, extraHolidays))
            return false;

        return MarketSessions.IsWithinSession(
            market, TimeOnly.FromDateTime(local.DateTime), IsHalfDay(market, date, extraHalfDays));
    }

    /// <summary>
    /// FR-03, #909, IADR-0380 決定3: その瞬間より**後**に来る最初の開場時刻（市場ローカルのオフセットで返す）。
    /// 閉場中の保護の空白を「いつまで続くか」まで含めて声に出すために使う。
    /// 走査上限（<see cref="NextOpenScanDays"/> 日）以内に見つからなければ <c>null</c>（未知の市場・長期休場）。
    /// </summary>
    public static DateTimeOffset? NextOpen(
        Market market,
        DateTimeOffset instant,
        IReadOnlySet<DateOnly>? extraHolidays = null,
        IReadOnlySet<DateOnly>? extraHalfDays = null)
    {
        var zone = ZoneOf(market);
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var today = DateOnly.FromDateTime(local.DateTime);

        for (var offset = 0; offset <= NextOpenScanDays; offset++)
        {
            var date = today.AddDays(offset);
            if (!IsTradingDay(market, date, extraHolidays))
                continue;

            // 当日は「今より後に始まるセッション」だけを見る（昼休み中なら後場の寄り付きが次の開場）。
            var from = offset == 0 ? TimeOnly.FromDateTime(local.DateTime) : TimeOnly.MinValue;
            if (MarketSessions.NextSessionStart(market, from, IsHalfDay(market, date, extraHalfDays)) is not { } start)
                continue;

            var naive = date.ToDateTime(start);
            if (zone.IsInvalidTime(naive))
                continue; // 夏時間の飛び（寄り付きは該当しないが、規則が変わっても壊れないようにする）

            return new DateTimeOffset(naive, zone.GetUtcOffset(naive));
        }

        return null;
    }

    private static bool IsTradingDay(Market market, DateOnly date, IReadOnlySet<DateOnly>? extraHolidays) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
        && !MarketHolidays.IsHoliday(market, date)
        && !(extraHolidays?.Contains(date) ?? false);

    private static bool IsHalfDay(Market market, DateOnly date, IReadOnlySet<DateOnly>? extraHalfDays) =>
        MarketHolidays.IsHalfDay(market, date) || (extraHalfDays?.Contains(date) ?? false);

    private static TimeZoneInfo Resolve(string windowsId, string ianaId) =>
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? windowsId : ianaId);
}
