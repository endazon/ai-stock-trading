using BacktestService.Domain;

namespace BacktestService.Features.Backtest;

// FR-15, FR-20, ADR-0008, IADR-0045: Stage 0 合格判定オーケストレーションの入力。
// Slice A/B の集計・試行台帳・性能行列・カットオフ材料を束ねる。
// DataAnonymized: 銘柄を匿名化して LLM 汚染を排したか。ADR-0008/検証条件①は「カットオフ後 または 匿名化」の OR。
public sealed record Stage0GateContext(
    BacktestMetrics BaselineMetrics,
    decimal DoubledCostTotalReturn,
    TrialLedger Trials,
    double[][] OverfittingPerformanceMatrix,
    int OverfittingPartitions,
    decimal WalkForwardOutOfSampleReturn,
    IReadOnlyList<PriceBar> Bars,
    DateOnly LlmTrainingCutoff,
    Stage0GateCriteria Criteria,
    bool DataAnonymized = false);

// FR-15, FR-20: Stage 0 判定の結果（ゲート・昇格推奨・算出した DSR/PBO・カットオフ充足）。
public sealed record Stage0Decision(
    Stage0GateResult Gate,
    StagePromotionRecommendation Promotion,
    double DeflatedSharpe,
    double ProbabilityOfBacktestOverfitting,
    bool DataCutoffSatisfied);

// FR-15, FR-20, ADR-0008, IADR-0045: Stage 0 合格判定を合成するオーケストレータ。
// DSR（試行台帳＋標本モーメント）・PBO（CSCV）・データカットオフを算出し、ゲート判定と Stage 1 昇格推奨に落とす。
public sealed class Stage0GateService
{
    public Stage0Decision Evaluate(Stage0GateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.BaselineMetrics);
        ArgumentNullException.ThrowIfNull(context.Trials);
        ArgumentNullException.ThrowIfNull(context.Criteria);

        // 選択戦略の 1 期間 Sharpe・歪度・尖度から、試行数で補正した DSR を算出する。
        var moments = SampleMomentsCalculator.Compute(context.BaselineMetrics.DailyReturns);
        var expectedMax = DeflatedSharpeRatio.ExpectedMaxSharpe(context.Trials.InSampleSharpeVariance(), context.Trials.Count);
        var dsr = DeflatedSharpeRatio.Compute(
            moments.SharpePerPeriod, moments.Count, moments.Skewness, moments.Kurtosis, expectedMax);

        var pbo = ProbabilityOfBacktestOverfitting.Compute(context.OverfittingPerformanceMatrix, context.OverfittingPartitions);
        // 検証条件①（ADR-0008/IADR-0044）は「全バーがカットオフ後 または 匿名化」の OR。匿名化済みなら日付は不問。
        var cutoffSatisfied = context.DataAnonymized || DataCutoffPolicy.IsAllAfterCutoff(context.Bars, context.LlmTrainingCutoff);

        var evaluation = new Stage0GateEvaluation(
            DeflatedSharpe: dsr,
            ProbabilityOfBacktestOverfitting: pbo,
            MaxDrawdown: context.BaselineMetrics.MaxDrawdown,
            DoubledCostTotalReturn: context.DoubledCostTotalReturn,
            WalkForwardOutOfSampleReturn: context.WalkForwardOutOfSampleReturn,
            TrialCount: context.Trials.Count,
            DataCutoffSatisfied: cutoffSatisfied);

        var gate = Stage0GateEvaluator.Evaluate(evaluation, context.Criteria);
        var promotion = Stage0Promotion.Evaluate(gate);

        return new Stage0Decision(gate, promotion, dsr, pbo, cutoffSatisfied);
    }
}
