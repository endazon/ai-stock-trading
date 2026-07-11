namespace AiStockTrading.Backtest.Domain;

// FR-15, FR-20, ADR-0008: Stage 0 合格判定の各条件。
public enum Stage0GateCheck
{
    DeflatedSharpe,
    Overfitting,
    MaxDrawdown,
    CostRobustness,
    WalkForward,
    TrialCount,
    DataCutoff,
}

// FR-15, ADR-0008, 06_daytrading-review §4: Stage 0 合格基準の閾値。
public sealed record Stage0GateCriteria(
    double MinDeflatedSharpe,
    double MaxProbabilityOfOverfitting,
    decimal MaxDrawdownTolerance,
    int MinTrials)
{
    // 既定閾値（DSR 0.95・PBO 0.5・最大DD 0.15・最小試行数 1）。実データでの較正は後続（IADR-0039）。
    public static Stage0GateCriteria Default => new(
        MinDeflatedSharpe: 0.95,
        MaxProbabilityOfOverfitting: 0.50,
        MaxDrawdownTolerance: 0.15m,
        MinTrials: 1);
}

// FR-15, ADR-0008: Stage 0 合格判定の入力（Slice A/B の集計・補正結果）。
public sealed record Stage0GateEvaluation(
    double DeflatedSharpe,
    double ProbabilityOfBacktestOverfitting,
    decimal MaxDrawdown,
    decimal BaselineTotalReturn,
    decimal DoubledCostTotalReturn,
    decimal WalkForwardOutOfSampleReturn,
    int TrialCount,
    bool DataCutoffSatisfied);

// FR-15: Stage 0 合格判定の結果。FailedChecks が空なら合格。
public sealed record Stage0GateResult(bool Passed, IReadOnlyList<Stage0GateCheck> FailedChecks);

// FR-15, FR-20, ADR-0008, 06_daytrading-review §4, IADR-0039: Stage 0 合格判定（純関数）。
// DSR 補正後のエッジ・過剰適合・最大DD・コスト2倍頑健性・ウォークフォワードOOS・試行数の 6 条件を合成する。
public static class Stage0GateEvaluator
{
    public static Stage0GateResult Evaluate(Stage0GateEvaluation evaluation, Stage0GateCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(criteria);

        var failed = new List<Stage0GateCheck>();

        // エッジ有意: DSR 補正後もエッジが正（閾値以上）。
        if (evaluation.DeflatedSharpe < criteria.MinDeflatedSharpe)
            failed.Add(Stage0GateCheck.DeflatedSharpe);
        // 過剰適合: PBO が閾値以下。
        if (evaluation.ProbabilityOfBacktestOverfitting > criteria.MaxProbabilityOfOverfitting)
            failed.Add(Stage0GateCheck.Overfitting);
        // 最大 DD: 許容内。
        if (evaluation.MaxDrawdown > criteria.MaxDrawdownTolerance)
            failed.Add(Stage0GateCheck.MaxDrawdown);
        // コスト頑健性: コストを 2 倍にしても期待値が正（06_daytrading-review §3.2）。
        if (evaluation.DoubledCostTotalReturn <= 0m)
            failed.Add(Stage0GateCheck.CostRobustness);
        // ウォークフォワード: OOS リターンが正。
        if (evaluation.WalkForwardOutOfSampleReturn <= 0m)
            failed.Add(Stage0GateCheck.WalkForward);
        // 試行数: 最小以上（過剰適合補正の前提）。
        if (evaluation.TrialCount < criteria.MinTrials)
            failed.Add(Stage0GateCheck.TrialCount);
        // データ健全性: 全バーが LLM 学習カットオフ後（または匿名化）＝汚染なし（FR-15 検証条件①）。
        if (!evaluation.DataCutoffSatisfied)
            failed.Add(Stage0GateCheck.DataCutoff);

        return new Stage0GateResult(failed.Count == 0, failed);
    }
}
