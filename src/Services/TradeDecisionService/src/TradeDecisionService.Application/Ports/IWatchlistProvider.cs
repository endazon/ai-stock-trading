using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.TradeDecision.Application.Ports;

// FR-02, IADR-0023: 定時サイクルで評価する監視銘柄の供給。暫定は構成ベース、実 watchlist（市場監視 #10）連携は後続。
public interface IWatchlistProvider
{
    IReadOnlyList<WatchedSymbol> GetWatchlist();
}

// 監視銘柄（銘柄コード・市場）。
public sealed record WatchedSymbol(string Symbol, Market Market);
