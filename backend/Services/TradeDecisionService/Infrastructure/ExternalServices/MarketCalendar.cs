using TradeDecisionService.Features.TradeDecision;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-02, UC-01, IADR-0023, #337, IADR-0245, #909, IADR-0380 決定1: 市場カレンダー。市場ローカル時刻（日本=JST、
// 米国=US Eastern）で「取引時間内か（週末・休場日でなく、かつ場中）」を判定する。
//
// 🔴 **判定の実体は共有カーネルの MarketHours であり、本型は構成の注入点だけを持つ**（#909）。
// 市場監視サービス（S1 の到達判定）も同じ MarketHours を引く —— 2 つのサービスが「今は開場か」で食い違うと、
// 片方が発注し片方が保護を止める組み合わせができる。
//
// 🔴 **［2026-09-23 / #909・IADR-0380 決定4 の挙動変更］米国市場の休場日・半日取引日が規則計算で入った。**
// 従来は構成注入だけで、`deploy/` に設定が 1 件も無かった（＝休場日ゼロ）ため、**独立記念日・感謝祭・
// グッドフライデー等にもサイクルが起動していた**。以後は起動しない。構成（`TradeCycle:Holidays:<Market>` /
// `TradeCycle:HalfDays:<Market>`）は臨時休場・臨時の半日を規則計算へ**足す**ためだけに使う（規則を外せない）。
public sealed class MarketCalendar(
    IReadOnlyDictionary<Market, IReadOnlySet<DateOnly>> holidays,
    IReadOnlyDictionary<Market, IReadOnlySet<DateOnly>>? halfDays = null) : IMarketCalendar
{
    private readonly IReadOnlyDictionary<Market, IReadOnlySet<DateOnly>> _halfDays =
        halfDays ?? new Dictionary<Market, IReadOnlySet<DateOnly>>();

    public bool IsOpen(Market market, DateTimeOffset instant) =>
        MarketHours.IsOpen(market, instant, Extra(holidays, market), Extra(_halfDays, market));

    private static IReadOnlySet<DateOnly>? Extra(
        IReadOnlyDictionary<Market, IReadOnlySet<DateOnly>> source, Market market) =>
        source.TryGetValue(market, out var dates) ? dates : null;
}
