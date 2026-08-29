using MarketMonitorService.Features.MarketMonitor;
using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Infrastructure.Persistence;

// FR-03: クールダウン（銘柄別最終トリガー時刻）のインメモリ実装。PostgreSQL 永続化は Slice B で差し替える。
public sealed class InMemoryCooldownStore : ICooldownStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string, Market), DateTimeOffset> _lastTriggered = [];

    public DateTimeOffset? GetLastTriggered(string symbol, Market market)
    {
        lock (_gate)
        {
            return _lastTriggered.TryGetValue((symbol, market), out var at) ? at : null;
        }
    }

    public void SetLastTriggered(string symbol, Market market, DateTimeOffset at)
    {
        lock (_gate)
        {
            _lastTriggered[(symbol, market)] = at;
        }
    }
}
