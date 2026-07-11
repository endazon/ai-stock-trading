using AiStockTrading.Backtest.Domain;

namespace AiStockTrading.Backtest.Application;

// FR-15, IADR-0043: 決定的な in-memory 過去データ源（テスト・検証用）。実データ源は後続 Issue。
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
