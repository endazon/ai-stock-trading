using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;

// FR-02, IADR-0023: 市場カレンダー。市場ローカル時刻（日本=JST、米国=US Eastern）で「取引日か（週末・休場日でない）」を判定する。
// 休場日は市場別に構成注入する（既定は空＝週末のみ）。祝日データ源の取り込み・場中時刻ガードは後続。
internal sealed class MarketCalendar(IReadOnlyDictionary<Market, IReadOnlySet<DateOnly>> holidays) : IMarketCalendar
{
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
        return !(holidays.TryGetValue(market, out var set) && set.Contains(date));
    }

    private static TimeZoneInfo Resolve(string windowsId, string ianaId) =>
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? windowsId : ianaId);
}
