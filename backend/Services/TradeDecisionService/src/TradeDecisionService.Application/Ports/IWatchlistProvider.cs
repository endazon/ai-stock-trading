using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Application.Ports;

// FR-02, IADR-0023/0095: 定時サイクルで評価する監視銘柄の供給。権威源は市場監視（#10 MarketMonitor の MonitoredSymbols）。
// 供給は s2s 同期照会（IADR-0095）で、実装未接続/失敗時は構成ベースの安全既定へ倒すため非同期ポートとする。
public interface IWatchlistProvider
{
    Task<IReadOnlyList<WatchedSymbol>> GetWatchlistAsync(CancellationToken cancellationToken = default);
}

// 監視銘柄（銘柄コード・市場）。
public sealed record WatchedSymbol(string Symbol, Market Market);
