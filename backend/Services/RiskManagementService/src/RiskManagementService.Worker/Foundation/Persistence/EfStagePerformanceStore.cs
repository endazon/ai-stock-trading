using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain;

namespace AiStockTrading.RiskManagement.Worker.Foundation.Persistence;

// FR-20, FR-15, IADR-0070: 段階別実績の EF 実装（単一行 upsert）。未記録時は fail-safe 既定
// （BacktestPassed=false ほか全 false/0）を返す＝既定で昇格を許可しない安全側。DbContext は scoped のため本ストアも scoped。
internal sealed class EfStagePerformanceStore(RiskManagementDbContext db) : IStagePerformanceStore
{
    public StagePerformance GetCurrent()
    {
        var row = db.StagePerformance.Find(SingletonKeys.Id);
        // fail-safe: 未記録なら既定（BacktestPassed=false）。実供給（後続）が Save するまで昇格は不可。
        return row is null ? new StagePerformance() : Map(row);
    }

    public void Save(StagePerformance performance)
    {
        ArgumentNullException.ThrowIfNull(performance);

        var row = db.StagePerformance.Find(SingletonKeys.Id);
        if (row is null)
        {
            db.StagePerformance.Add(Map(performance));
        }
        else
        {
            row.BacktestPassed = performance.BacktestPassed;
            row.BacktestMaxDrawdownRatio = performance.BacktestMaxDrawdownRatio;
            row.ObservedMaxDrawdownRatio = performance.ObservedMaxDrawdownRatio;
            row.PaperDeviationExplained = performance.PaperDeviationExplained;
            row.ControlViolationCount = performance.ControlViolationCount;
            row.SlippageAndCostWithinExpected = performance.SlippageAndCostWithinExpected;
            row.DailyLossLimitRespected = performance.DailyLossLimitRespected;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        db.SaveChanges();
    }

    private static StagePerformance Map(StagePerformanceRow row) => new()
    {
        BacktestPassed = row.BacktestPassed,
        BacktestMaxDrawdownRatio = row.BacktestMaxDrawdownRatio,
        ObservedMaxDrawdownRatio = row.ObservedMaxDrawdownRatio,
        PaperDeviationExplained = row.PaperDeviationExplained,
        ControlViolationCount = row.ControlViolationCount,
        SlippageAndCostWithinExpected = row.SlippageAndCostWithinExpected,
        DailyLossLimitRespected = row.DailyLossLimitRespected,
    };

    private static StagePerformanceRow Map(StagePerformance performance) => new()
    {
        Id = SingletonKeys.Id,
        BacktestPassed = performance.BacktestPassed,
        BacktestMaxDrawdownRatio = performance.BacktestMaxDrawdownRatio,
        ObservedMaxDrawdownRatio = performance.ObservedMaxDrawdownRatio,
        PaperDeviationExplained = performance.PaperDeviationExplained,
        ControlViolationCount = performance.ControlViolationCount,
        SlippageAndCostWithinExpected = performance.SlippageAndCostWithinExpected,
        DailyLossLimitRespected = performance.DailyLossLimitRespected,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
