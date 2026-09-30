using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Tests;

// FR-04, #1118, IADR-0467: 日足の試験の共通データ（前営業日で終わる連続した取引日の足）。
internal static class DailyBarsTestData
{
    // 2026-09-29（火）14:00 UTC ＝ 米国東部 10:00（場中）。取引日 2026-09-29、前営業日 2026-09-28（月）。
    public static readonly DateTimeOffset TuesdayMorning = new(2026, 9, 29, 14, 0, 0, TimeSpan.Zero);
    public static readonly DateOnly Tuesday = new(2026, 9, 29);
    public static readonly DateOnly Monday = new(2026, 9, 28);

    /// <summary>lastDate で終わる連続した取引日（週末・規則計算の休場日を除く）の足を、古い順に count 本。出来高は volumes（古い順）。</summary>
    public static List<DailyBar> Bars(DateOnly lastDate, params long[] volumes)
    {
        var dates = new List<DateOnly> { lastDate };
        while (dates.Count < volumes.Length)
            dates.Add(MarketTradingDays.PreviousTradingDay(Market.UnitedStates, dates[^1]));
        dates.Reverse();
        return [.. dates.Select((d, i) => new DailyBar(d, 100m, 101m, 99m, 100.5m, volumes[i]))];
    }

    public static long[] Repeat(long volume, int count) => [.. Enumerable.Repeat(volume, count)];

    public static long[] Then(long[] head, params long[] tail) => [.. head, .. tail];

    public static ConfirmedDailyBars Confirmed(IReadOnlyList<DailyBar> bars) => new(Tuesday, Monday, bars);
}

internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
