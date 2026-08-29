using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Domain;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-20, FR-15, IADR-0070: 段階別実績のインメモリ実装（ユニット試験・非 relational 用）。
// 未記録時は fail-safe 既定（BacktestPassed=false）＝昇格を許可しない安全側。
public sealed class InMemoryStagePerformanceStore : IStagePerformanceStore
{
    private readonly Lock _gate = new();
    private StagePerformance _performance = new();

    public StagePerformance GetCurrent()
    {
        lock (_gate)
        {
            return _performance;
        }
    }

    public void Save(StagePerformance performance)
    {
        ArgumentNullException.ThrowIfNull(performance);
        lock (_gate)
        {
            _performance = performance;
        }
    }
}
