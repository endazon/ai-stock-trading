using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Configuration;

namespace AiStockTrading.TradeDecision.Worker.Composable.Adapters;

// FR-02, IADR-0023: 定時サイクルで評価する監視銘柄を構成（TradeCycle:Watchlist）から供給する暫定実装。
// 実 watchlist（市場監視 #10 の監視銘柄）連携は後続。
internal sealed class ConfigurationWatchlistProvider(IConfiguration configuration) : IWatchlistProvider
{
    public IReadOnlyList<WatchedSymbol> GetWatchlist()
    {
        var entries = configuration.GetSection("TradeCycle:Watchlist").Get<List<WatchlistEntry>>() ?? [];
        return [.. entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Symbol))
            .Select(e => new WatchedSymbol(e.Symbol!, e.Market))];
    }

    // 構成バインド用（Market は列挙名でバインドされる）。
    private sealed class WatchlistEntry
    {
        public string? Symbol { get; set; }

        public Market Market { get; set; }
    }
}
