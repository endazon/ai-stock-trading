using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.MarketMonitor.Domain;

// FR-03, FR-13: 監視対象の銘柄（銘柄コード・市場）。設定で増減できる。
public record MonitoredSymbol(string Symbol, Market Market);
