using RiskManagementService.Domain;

namespace RiskManagementService.Application.Services;

// FR-20, FR-10, UC-06, ADR-0008, IADR-0103, #164: 段階別実績（StagePerformance）の運用系フィールドのうち
// 実DD（ObservedMaxDrawdownRatio）だけを更新する純関数。DB・Clock 非依存。
//
// フィールド所有権の分離（IADR-0089 の鏡像）: 単一行 StagePerformance は複数の供給源が更新する。
// backtest 由来（BacktestPassed / BacktestMaxDrawdownRatio）は BacktestEvaluatedProjectionConsumer が、
// 実DD は ObservedDrawdownRefreshService（本射影の呼び出し元）が所有する。どちらも read-modify-write で
// 自分の所有フィールドだけを `with` 更新し、他の供給源のフィールドは温存する。
public static class StagePerformanceProjection
{
    // 観測した DD を単調非減少（最大値 latch）で反映する。
    // 「観測した最大ドローダウン比率」（撤退基準 ADR-0008 の実測値）であり、資金が回復して現在 DD が下がっても
    // 過去に到達した谷は消えない。負値（異常入力）は 0 として無視し、既存の最大値を壊さない。
    public static StagePerformance WithObservedDrawdown(StagePerformance current, decimal sampledDrawdownRatio)
    {
        ArgumentNullException.ThrowIfNull(current);

        var sampled = Math.Max(0m, sampledDrawdownRatio);
        return current with
        {
            ObservedMaxDrawdownRatio = Math.Max(current.ObservedMaxDrawdownRatio, sampled),
        };
    }

    // 実DD の観測窓をリセットする（差し戻し＝再検証のやり直しで観測を区切る）。実DD 以外は温存する。
    public static StagePerformance WithoutObservedDrawdown(StagePerformance current)
    {
        ArgumentNullException.ThrowIfNull(current);

        return current with { ObservedMaxDrawdownRatio = 0m };
    }
}
