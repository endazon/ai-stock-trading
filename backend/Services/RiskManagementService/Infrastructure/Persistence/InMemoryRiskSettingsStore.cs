using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Domain;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-10, FR-17: 設定ストアのインメモリ実装。初期値は TradingDefaults（全体前提条件 §5）。
// 実運用の PostgreSQL 設定ストア（バージョン管理）は Slice B で差し替える（IRiskSettingsStore で吸収）。
public sealed class InMemoryRiskSettingsStore : IRiskSettingsStore
{
    private readonly Lock _gate = new();
    private RiskManagementSettings _current;

    // FR-19, #1220, IADR-0511: 商品種別設定の改訂番号。保存のたびにストアが進める（呼び出し側は書けない）。
    private long _productTypesRevision = ProductTypeSettingsRevision.Initial;

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
            _productTypesRevision = ProductTypeSettingsRevision.Next(
                _current.Guard.EnabledProductTypes, settings.Guard.EnabledProductTypes, _productTypesRevision);
            _current = settings;
        }
    }

    // インメモリでは番号を知らない書き手が存在しないため、番号は常にある（Initial＝1 から）。
    public long? GetProductTypesRevision() => EnsureProductTypesRevision();

    public long EnsureProductTypesRevision()
    {
        lock (_gate)
        {
            return _productTypesRevision;
        }
    }
}
