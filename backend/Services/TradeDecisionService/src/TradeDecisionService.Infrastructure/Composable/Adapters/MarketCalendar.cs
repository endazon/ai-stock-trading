using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;

// FR-02, UC-01, IADR-0023, #337, IADR-0245: 市場カレンダー。市場ローカル時刻（日本=JST、米国=US Eastern）で
// 「取引時間内か（週末・休場日でなく、かつ場中）」を判定する。
//
// 🔴 **市場時刻の判定はすべて市場ローカル時刻で行う**（米国市場は米国東部時間）。サマータイムの切り替えで
// 日本時間との差が 1 時間ずれるため、**固定のオフセットで換算しない**（計画 04_workflows/01 の明文）。
// DST の切替は TimeZoneInfo が吸収する——同じ現地時刻（例: 9:30 ET）は EST でも EDT でも同じ場中判定へ写る。
//
// 休場日・半日取引日は市場別に構成注入する（TradeCycle:Holidays:<Market> / TradeCycle:HalfDays:<Market>。
// 既定は空＝週末と時間帯のみ）。場中の時間帯判定は MarketSessions（Domain 純関数）が単一情報源である。
internal sealed class MarketCalendar(
    IReadOnlyDictionary<Market, IReadOnlySet<DateOnly>> holidays,
    IReadOnlyDictionary<Market, IReadOnlySet<DateOnly>>? halfDays = null) : IMarketCalendar
{
    private readonly IReadOnlyDictionary<Market, IReadOnlySet<DateOnly>> _halfDays =
        halfDays ?? new Dictionary<Market, IReadOnlySet<DateOnly>>();

    // クロスプラットフォームのため OS で TZ ID を切り替える（SystemClock と同方針）。
    private static readonly TimeZoneInfo JapanZone = Resolve("Tokyo Standard Time", "Asia/Tokyo");
    private static readonly TimeZoneInfo UsEasternZone = Resolve("Eastern Standard Time", "America/New_York");

    public bool IsOpen(Market market, DateTimeOffset instant)
    {
        var zone = market == Market.Japan ? JapanZone : UsEasternZone;
        var local = TimeZoneInfo.ConvertTime(instant, zone);

        if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        var date = DateOnly.FromDateTime(local.DateTime);
        if (holidays.TryGetValue(market, out var closed) && closed.Contains(date))
            return false;

        // #337: 半日取引日（米国のみ実在。感謝祭翌日・クリスマスイブ等）は終了時刻が前倒しになる。
        var isHalfDay = _halfDays.TryGetValue(market, out var half) && half.Contains(date);
        return MarketSessions.IsWithinSession(market, TimeOnly.FromDateTime(local.DateTime), isHalfDay);
    }

    private static TimeZoneInfo Resolve(string windowsId, string ianaId) =>
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? windowsId : ianaId);
}
