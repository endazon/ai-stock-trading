using AiStockTrading.MarketMonitor.Domain;

namespace AiStockTrading.MarketMonitor.Application.Ports;

// FR-03, FR-13: 監視設定（監視銘柄・変動閾値・クールダウン）の取得・保存。実運用では PostgreSQL 設定ストア（Slice B）。
// 変更は利用者のみ（設定エンドポイントの OwnerOnly 認可で担保）。
public interface IMonitoredSymbolStore
{
    MarketMonitorSettings GetSettings();

    void Save(MarketMonitorSettings settings);
}
