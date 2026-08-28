using TradeDecisionService.Application.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Configuration;

namespace TradeDecisionService.Infrastructure.Adapters;

// FR-02, IADR-0023/0095: 定時サイクルで評価する監視銘柄を構成（TradeCycle:Watchlist）から供給する。
// 権威源は市場監視（#10 MarketMonitor の watchlist・IADR-0088）だが、本アダプタは MarketMonitor:BaseUrl 未設定時の
// 後方互換フォールバック、および HttpWatchlistProvider の照会失敗時の fail-safe 既定 watchlist として使う（IADR-0095）。
internal sealed class ConfigurationWatchlistProvider(IConfiguration configuration) : IWatchlistProvider
{
    public Task<IReadOnlyList<WatchedSymbol>> GetWatchlistAsync(CancellationToken cancellationToken = default)
    {
        var entries = configuration.GetSection("TradeCycle:Watchlist").Get<List<WatchlistEntry>>() ?? [];
        IReadOnlyList<WatchedSymbol> result = [.. entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Symbol))
            .Select(e => new WatchedSymbol(e.Symbol!, e.Market))];
        return Task.FromResult(result);
    }

    // 構成バインド用（Market は列挙名でバインドされる）。
    private sealed class WatchlistEntry
    {
        public string? Symbol { get; set; }

        public Market Market { get; set; }
    }
}
