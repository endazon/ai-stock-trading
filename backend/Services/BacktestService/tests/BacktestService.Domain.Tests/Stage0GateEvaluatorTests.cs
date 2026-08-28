using AwesomeAssertions;
using Xunit;

namespace BacktestService.Domain.Tests;

// FR-15, FR-20, ADR-0008, 06_daytrading-review §4: Stage 0 合格判定（7 条件の合成）を検証する。
public class Stage0GateEvaluatorTests
{
    // 全条件を満たす評価（DSR 0.99・PBO 0.1・最大DD 0.08・コスト2倍 +・OOS +・試行 20）。
    // 試行数は較正後の下限（Stage0GateCriteria.Default.MinTrials=20・#208/IADR-0110）ちょうどに置き、
    // 下限が上がったときに本ヘルパが黙って不合格側へ倒れないよう既定値へ追随させる。
    private static Stage0GateEvaluation Passing() => new(
        DeflatedSharpe: 0.99,
        ProbabilityOfBacktestOverfitting: 0.10,
        MaxDrawdown: 0.08m,
        DoubledCostTotalReturn: 0.12m,
        WalkForwardOutOfSampleReturn: 0.05m,
        TrialCount: Stage0GateCriteria.Default.MinTrials,
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

    // #208, IADR-0110: 較正で下限が 1 → 20 へ上がった。過少申告された台帳（1〜2 件）は、
    // 他の 6 条件を満たしていても不合格になる（多重検定補正が効かない台帳を通さない）。
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(19)]
    public void 較正後の下限未満の台帳は他条件を満たしても不合格(int trialCount)
    {
        var eval = Passing() with { TrialCount = trialCount };

        var result = Stage0GateEvaluator.Evaluate(eval, Criteria);

        result.Passed.Should().BeFalse();
        result.FailedChecks.Should().Contain(Stage0GateCheck.TrialCount);
    }

    [Fact]
    public void データカットオフ不成立なら不合格_LLM汚染疑い()
    {
        var eval = Passing() with { DataCutoffSatisfied = false };
        Stage0GateEvaluator.Evaluate(eval, Criteria).FailedChecks.Should().Contain(Stage0GateCheck.DataCutoff);
    }

    [Fact]
    public void しきい値ちょうどは合格側に倒れる_境界値()
    {
        // DSR=0.95（≥）・PBO=0.50（≤）・最大DD=許容値ちょうど（≤）はいずれも合格（非等号比較の境界固定）。
        // 最大DD は既定値から引く（#333・ADR-0018 決定2 で 0.15 → 0.10 へ厳格化した。値を直書きすると
        // 閾値の変更時に「境界のつもりが境界でない」テストになる）。
        var eval = Passing() with
        {
            DeflatedSharpe = 0.95,
            ProbabilityOfBacktestOverfitting = 0.50,
            MaxDrawdown = Criteria.MaxDrawdownTolerance,
        };
        var result = Stage0GateEvaluator.Evaluate(eval, Criteria);
        result.Passed.Should().BeTrue();
        result.FailedChecks.Should().BeEmpty();
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
