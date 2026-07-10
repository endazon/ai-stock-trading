using AiStockTrading.MarketMonitor.Domain;

namespace AiStockTrading.MarketMonitor.Application.Ports;

// FR-03, FR-13: 監視設定（監視銘柄・変動閾値・クールダウン）の取得。実運用では PostgreSQL 設定ストア（Slice B）。
public interface IMonitoredSymbolStore
{
    MarketMonitorSettings GetSettings();
}
