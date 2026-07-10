using AiStockTrading.MarketMonitor.Application.Ports;

namespace AiStockTrading.MarketMonitor.Worker.Composable.Adapters;

// FR-03: 平日判定の最小市場カレンダー（土日は閉場）。時間帯・祝日を含む正確な市場カレンダーは #21 で差し替える
// （ポート IMarketSchedule で吸収）。基準タイムゾーンは Slice B では UTC 日付の曜日で近似する。
internal sealed class WeekdayMarketSchedule : IMarketSchedule
{
    public bool IsOpen(DateTimeOffset instant) =>
        instant.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
}
