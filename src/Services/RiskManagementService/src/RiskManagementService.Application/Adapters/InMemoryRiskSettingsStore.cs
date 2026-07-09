using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain;

namespace AiStockTrading.RiskManagement.Application.Adapters;

// FR-10, FR-17: 設定ストアのインメモリ実装。初期値は TradingDefaults（全体前提条件 §5）。
// 実運用の PostgreSQL 設定ストア（バージョン管理）は Slice B で差し替える（IRiskSettingsStore で吸収）。
public sealed class InMemoryRiskSettingsStore : IRiskSettingsStore
{
    private readonly Lock _gate = new();
    private RiskManagementSettings _current;

    public InMemoryRiskSettingsStore(RiskManagementSettings? initial = null)
    {
        _current = initial ?? TradingDefaults.CreateSettings();
    }

    public RiskManagementSettings GetCurrent()
    {
        lock (_gate)
        {
            return _current;
        }
    }

    public void Save(RiskManagementSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _current = settings;
        }
    }
}
