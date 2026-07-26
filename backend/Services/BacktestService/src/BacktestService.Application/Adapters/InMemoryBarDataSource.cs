using AiStockTrading.Backtest.Domain;

namespace AiStockTrading.Backtest.Application;

// FR-15, IADR-0043: 決定的な in-memory 過去データ源。
//
// #208 / IADR-0105 以降、**用途はテスト・検証（シミュレータやゲートの決定的な単体検証）に限る**。
// 実過去データ源から取得したバーを本番経路で供給するのは MaterializedBarDataSource であり、
// 取得は IHistoricalBarSource（既定 no-op・provider 明示指定で opt-in）が担う。
public sealed class InMemoryBarDataSource : IBarDataSource
{
    private readonly IReadOnlyList<PriceBar> _bars;

    public InMemoryBarDataSource(IEnumerable<PriceBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        _bars = bars.ToList();
    }

    public IReadOnlyList<PriceBar> GetBars(DateOnly from, DateOnly to) =>
        _bars.Where(b => b.Date >= from && b.Date <= to).ToList();
}
