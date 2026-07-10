using AiStockTrading.MarketMonitor.Application.Ports;
using AiStockTrading.MarketMonitor.Domain;

namespace AiStockTrading.MarketMonitor.Application.Adapters;

// FR-03, FR-13: 監視設定のインメモリ実装。既定は MonitorDefaults（閾値 3%・クールダウン 15 分）。
// 実運用の PostgreSQL 設定ストアは Slice B で差し替える。
public sealed class InMemoryMonitoredSymbolStore(MarketMonitorSettings? initial = null) : IMonitoredSymbolStore
{
    private readonly Lock _gate = new();
    private MarketMonitorSettings _settings = initial ?? MonitorDefaults.CreateSettings();

    public MarketMonitorSettings GetSettings()
    {
        lock (_gate)
        {
            return _settings;
        }
    }

    public void Save(MarketMonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _settings = settings;
        }
    }
}
