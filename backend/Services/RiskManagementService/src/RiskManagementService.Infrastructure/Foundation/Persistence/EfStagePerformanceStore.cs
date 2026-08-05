using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain;

namespace AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence;

// FR-20, FR-15, IADR-0070: 段階別実績の EF 実装（単一行 upsert）。未記録時は fail-safe 既定
// （BacktestPassed=false ほか全 false/0）を返す＝既定で昇格を許可しない安全側。DbContext は scoped のため本ストアも scoped。
//
// IADR-0103 決定7（新しい供給源を足すときの前提）: 本行は複数の供給源が read-modify-write で更新する
// （backtest 由来の射影 / 実DD ドライバ / 差し戻しリセット）。各供給源が担当外フィールドを巻き戻さないのは、
// 下の Save が Find で「同一スコープの追跡済みエンティティ」を取り、GetCurrent() 時点のオリジナル値との差分だけが
// Modified になる＝担当外列が UPDATE に載らないためである。**読みと書きは必ず同一スコープ（同一 DbContext）で行うこと。**
// 別スコープで読んだ値を持ち込むと Find が DB を再読して全列 Modified になり、この保証が静かに崩れる。
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
            row.Stage1QualifiedTradingDays = performance.Stage1QualifiedTradingDays;
            row.Stage1TradeCount = performance.Stage1TradeCount;
            row.Stage1ExcludedInternalPaperDays = performance.Stage1ExcludedInternalPaperDays;
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
        Stage1QualifiedTradingDays = row.Stage1QualifiedTradingDays,
        Stage1TradeCount = row.Stage1TradeCount,
        Stage1ExcludedInternalPaperDays = row.Stage1ExcludedInternalPaperDays,
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
        Stage1QualifiedTradingDays = performance.Stage1QualifiedTradingDays,
        Stage1TradeCount = performance.Stage1TradeCount,
        Stage1ExcludedInternalPaperDays = performance.Stage1ExcludedInternalPaperDays,
        ControlViolationCount = performance.ControlViolationCount,
        SlippageAndCostWithinExpected = performance.SlippageAndCostWithinExpected,
        DailyLossLimitRespected = performance.DailyLossLimitRespected,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
