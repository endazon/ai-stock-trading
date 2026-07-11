using FluentAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Domain.Tests;

// FR-15, FR-20, ADR-0008, 06_daytrading-review §4: Stage 0 合格判定（6 条件の合成）を検証する。
public class Stage0GateEvaluatorTests
{
    // 全条件を満たす評価（DSR 0.99・PBO 0.1・最大DD 0.08・コスト2倍 +・OOS +・試行 10）。
    private static Stage0GateEvaluation Passing() => new(
        DeflatedSharpe: 0.99,
        ProbabilityOfBacktestOverfitting: 0.10,
        MaxDrawdown: 0.08m,
        BaselineTotalReturn: 0.25m,
        DoubledCostTotalReturn: 0.12m,
        WalkForwardOutOfSampleReturn: 0.05m,
        TrialCount: 10,
        DataCutoffSatisfied: true);

    private static readonly Stage0GateCriteria Criteria = Stage0GateCriteria.Default;

    [Fact]
    public void 全条件を満たすと合格_不合格理由なし()
    {
        var result = Stage0GateEvaluator.Evaluate(Passing(), Criteria);
        result.Passed.Should().BeTrue();
        result.FailedChecks.Should().BeEmpty();
    }

    [Fact]
    public void DSRが閾値未満なら不合格_エッジ有意でない()
    {
        var eval = Passing() with { DeflatedSharpe = 0.80 };
        var result = Stage0GateEvaluator.Evaluate(eval, Criteria);
        result.Passed.Should().BeFalse();
        result.FailedChecks.Should().Contain(Stage0GateCheck.DeflatedSharpe);
    }

    [Fact]
    public void PBOが閾値超なら不合格_過剰適合()
    {
        var eval = Passing() with { ProbabilityOfBacktestOverfitting = 0.60 };
        Stage0GateEvaluator.Evaluate(eval, Criteria).FailedChecks.Should().Contain(Stage0GateCheck.Overfitting);
    }

    [Fact]
    public void 最大DDが許容超なら不合格()
    {
        var eval = Passing() with { MaxDrawdown = 0.20m };
        Stage0GateEvaluator.Evaluate(eval, Criteria).FailedChecks.Should().Contain(Stage0GateCheck.MaxDrawdown);
    }

    [Fact]
    public void コスト2倍で非正なら不合格_コスト頑健でない()
    {
        var eval = Passing() with { DoubledCostTotalReturn = 0m };
        Stage0GateEvaluator.Evaluate(eval, Criteria).FailedChecks.Should().Contain(Stage0GateCheck.CostRobustness);
    }

    [Fact]
    public void ウォークフォワードOOSが非正なら不合格()
    {
        var eval = Passing() with { WalkForwardOutOfSampleReturn = -0.01m };
        Stage0GateEvaluator.Evaluate(eval, Criteria).FailedChecks.Should().Contain(Stage0GateCheck.WalkForward);
    }

    [Fact]
    public void 試行数が最小未満なら不合格()
    {
        var eval = Passing() with { TrialCount = 0 };
        Stage0GateEvaluator.Evaluate(eval, Criteria).FailedChecks.Should().Contain(Stage0GateCheck.TrialCount);
    }

    [Fact]
    public void データカットオフ不成立なら不合格_LLM汚染疑い()
    {
        var eval = Passing() with { DataCutoffSatisfied = false };
        Stage0GateEvaluator.Evaluate(eval, Criteria).FailedChecks.Should().Contain(Stage0GateCheck.DataCutoff);
    }

    [Fact]
    public void 複数条件の違反を全て列挙する()
    {
        var eval = Passing() with { DeflatedSharpe = 0.5, MaxDrawdown = 0.3m };
        var result = Stage0GateEvaluator.Evaluate(eval, Criteria);
        result.Passed.Should().BeFalse();
        result.FailedChecks.Should().Contain(Stage0GateCheck.DeflatedSharpe)
            .And.Contain(Stage0GateCheck.MaxDrawdown);
    }
}
