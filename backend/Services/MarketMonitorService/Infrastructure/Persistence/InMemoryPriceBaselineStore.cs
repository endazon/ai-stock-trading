using MarketMonitorService.Features.MarketMonitor;
using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Infrastructure.Persistence;

// FR-03: 基準値（前回判断時点価格）のインメモリ実装。PostgreSQL 永続化は Slice B で差し替える。
public sealed class InMemoryPriceBaselineStore : IPriceBaselineStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string, Market), decimal> _baselines = [];

    public decimal? GetBaseline(string symbol, Market market)
    {
        lock (_gate)
        {
            return _baselines.TryGetValue((symbol, market), out var price) ? price : null;
        }
    }

    public void SetBaseline(string symbol, Market market, decimal price)
    {
        lock (_gate)
        {
            _baselines[(symbol, market)] = price;
        }
    }
}
