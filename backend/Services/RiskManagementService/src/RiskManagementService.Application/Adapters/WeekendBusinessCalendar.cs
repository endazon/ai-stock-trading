using AiStockTrading.RiskManagement.Application.Ports;

namespace AiStockTrading.RiskManagement.Application.Adapters;

// FR-10: 週末（土日）をスキップする最小の営業日カレンダー。祝日データを含む市場カレンダーは #21 で差し替える
// （ポート IBusinessCalendar で吸収）。ロックアウトの「翌営業日」算出に用いる。
public sealed class WeekendBusinessCalendar : IBusinessCalendar
{
    public DateOnly NextBusinessDay(DateOnly date)
    {
        var next = date.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            next = next.AddDays(1);
        }

        return next;
    }

    // #166, #21: 週末（土日）でなければ営業日。祝日データを含む市場カレンダーは #21 で差し替える。
    public bool IsBusinessDay(DateOnly date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
}
