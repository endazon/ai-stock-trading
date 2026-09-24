using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using MarketMonitorService.Features.MarketMonitor;

namespace MarketMonitorService.Infrastructure.ExternalServices;

// FR-03, FR-10, FR-01, UC-02, #909, #21, IADR-0380 決定1・決定4: 市場カレンダー。市場ローカル時刻
// （米国=US Eastern の 9:30–16:00、東証=JST の 9:00–11:30 / 12:30–15:30）で場中かを判定する。
//
// 置き換え前は `WeekdayMarketSchedule`（UTC の曜日が土日でなければ開場）だった。**引けの 5 時間後も巡回が回り、
// 凍った終値で S1 の到達判定が成立し得た**（#909 の実測）。
//
// 🔴 **判定の実体は共有カーネルの MarketHours であり、本型は構成の注入点だけを持つ。**
// 取引判断サービスの `MarketCalendar` も同じ実体を引く（2 つのサービスが「今は開場か」で食い違わない）。
// 休場日・半日取引日は規則計算が基礎で、構成（`Monitor:Holidays:<Market>` / `Monitor:HalfDays:<Market>`、
// いずれも `["yyyy-MM-dd", ...]`）は臨時休場を**足す**ためだけに使う —— 規則を**外せない**のは、
// 設定ミスが「閉場中に終値で損切りを回す」側（#909 の事故）へ倒れないようにするためである。
public sealed class MarketHoursSchedule(
    IReadOnlyDictionary<Market, IReadOnlySet<DateOnly>> holidays,
    IReadOnlyDictionary<Market, IReadOnlySet<DateOnly>>? halfDays = null) : IMarketSchedule
{
    private readonly IReadOnlyDictionary<Market, IReadOnlySet<DateOnly>> _halfDays =
        halfDays ?? new Dictionary<Market, IReadOnlySet<DateOnly>>();

    public bool IsOpen(Market market, DateTimeOffset instant) =>
        MarketHours.IsOpen(market, instant, Extra(holidays, market), Extra(_halfDays, market));

    public DateTimeOffset? NextOpen(Market market, DateTimeOffset instant) =>
        MarketHours.NextOpen(market, instant, Extra(holidays, market), Extra(_halfDays, market));

    private static IReadOnlySet<DateOnly>? Extra(
        IReadOnlyDictionary<Market, IReadOnlySet<DateOnly>> source, Market market) =>
        source.TryGetValue(market, out var dates) ? dates : null;
}
