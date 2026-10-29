using MarketMonitorService.Features.MarketMonitor;
using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Infrastructure.Persistence;

// FR-03: 基準値（前回判断時点価格）のインメモリ実装。PostgreSQL 永続化は Slice B で差し替える。
public sealed class InMemoryPriceBaselineStore : IPriceBaselineStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string, Market), PriceBaseline> _baselines = [];

    public decimal? GetBaseline(string symbol, Market market) => GetBaselineEntry(symbol, market)?.Price;

    // #1487, IADR-0596: 置いた時刻つきで返す（3 引数で置いた値は時刻が分からない＝null）。
    public PriceBaseline? GetBaselineEntry(string symbol, Market market)
    {
        lock (_gate)
        {
            return _baselines.TryGetValue((symbol, market), out var entry) ? entry : null;
        }
    }

    public void SetBaseline(string symbol, Market market, decimal price) => Set(symbol, market, new PriceBaseline(price, null));

    /// <summary>#1487, IADR-0596: 置いた時刻を指定して置く（試験が前の取引日の基準値を作るため）。</summary>
    public void SetBaseline(string symbol, Market market, decimal price, DateTimeOffset setAt) =>
        Set(symbol, market, new PriceBaseline(price, setAt));

    private void Set(string symbol, Market market, PriceBaseline entry)
    {
        lock (_gate)
        {
            _baselines[(symbol, market)] = entry;
        }
    }
}
