using BacktestService.Domain;
using AwesomeAssertions;
using Xunit;

namespace BacktestService.Tests;

// FR-15, FR-20, ADR-0008, 06_daytrading-review §4: Stage 0 合格判定（7 条件の合成）を検証する。
public class Stage0GateEvaluatorTests
{
    // 全条件を満たす評価（DSR 0.99・PBO 0.1・最大DD 0.08・コスト2倍 +・OOS +・試行 20）。
    // 試行数は較正後の下限（Stage0GateCriteria.Default.MinTrials=20・#208/IADR-0110）ちょうどに置き、
    // 下限が上がったときに本ヘルパが黙って不合格側へ倒れないよう既定値へ追随させる。
    private static Stage0GateEvaluation Passing() => new(
        DeflatedSharpe: 0.99,
        Pbo: new PboVerdict.Evaluated(0.10),
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
        var eval = Passing() with { Pbo = new PboVerdict.Evaluated(0.60) };
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
    //
    // 🔴 **陰性対照（ADR-0039 決定2・#777・IADR-0337 決定2）**: 本 Theory の入力は PBO を**評価した**
    // （探索がある）経路である。**そこでは 2〜19 本でも下限 20 を緩めない。** 探索を持たない試行 1 本の
    // 経路（PBO 評価不能）だけが下限の適用外であり、「試行が少ない」ことだけを理由に緩めたのではない。
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
            Pbo = new PboVerdict.Evaluated(0.50),
            MaxDrawdown = Criteria.MaxDrawdownTolerance,
        };
        var result = Stage0GateEvaluator.Evaluate(eval, Criteria);
        result.Passed.Should().BeTrue();
        result.FailedChecks.Should().BeEmpty();
    }

    // ---- ADR-0039 決定1・決定2（#777・IADR-0337）: 探索を持たない記録再生の扱い ----

    // 探索を持たない（試行 1 本）評価。PBO は **評価不能** であり、下限 20 も適用されない。
    private static Stage0GateEvaluation WithoutSearch() => Passing() with
    {
        Pbo = new PboVerdict.NotEvaluable(PboNotEvaluableReason.NoSearchSingleTrial),
        TrialCount = 1,
    };

    // **陽性**: PBO が評価不能なら、過剰適合条件は合否の根拠から外れ、試行数の下限も適用されない。
    // 残る条件を満たしていれば合格する（ADR-0039 決定1・決定2）。
    [Fact]
    public void 探索を持たない試行1本はPBO条件と試行数の下限が合否の根拠から外れる()
    {
        var result = Stage0GateEvaluator.Evaluate(WithoutSearch(), Criteria);

        result.FailedChecks.Should().NotContain(Stage0GateCheck.Overfitting);
        result.FailedChecks.Should().NotContain(Stage0GateCheck.TrialCount);
        result.Passed.Should().BeTrue();
    }

    // 🔴 **否定形（最重要）**: 評価不能は「合格」ではない —— **他の条件が落ちれば落ちる。**
    // 過剰適合条件が合否の根拠から外れたことを「PBO は基準を満たした」と読ませない。
    [Theory]
    [InlineData(nameof(Stage0GateCheck.DeflatedSharpe))]
    [InlineData(nameof(Stage0GateCheck.MaxDrawdown))]
    [InlineData(nameof(Stage0GateCheck.CostRobustness))]
    [InlineData(nameof(Stage0GateCheck.WalkForward))]
    [InlineData(nameof(Stage0GateCheck.DataCutoff))]
    public void PBOが評価不能でも残る条件が落ちれば不合格になる(string failing)
    {
        var eval = failing switch
        {
            nameof(Stage0GateCheck.DeflatedSharpe) => WithoutSearch() with { DeflatedSharpe = 0.5 },
            nameof(Stage0GateCheck.MaxDrawdown) => WithoutSearch() with { MaxDrawdown = 0.30m },
            nameof(Stage0GateCheck.CostRobustness) => WithoutSearch() with { DoubledCostTotalReturn = -0.01m },
            nameof(Stage0GateCheck.WalkForward) => WithoutSearch() with { WalkForwardOutOfSampleReturn = -0.01m },
            _ => WithoutSearch() with { DataCutoffSatisfied = false },
        };

        var result = Stage0GateEvaluator.Evaluate(eval, Criteria);

        result.Passed.Should().BeFalse();
        result.FailedChecks.Should().Contain(Enum.Parse<Stage0GateCheck>(failing));
        // 評価不能な PBO は、合否がどちらへ転んでも未達理由には載らない（測っていないのだから）。
        result.FailedChecks.Should().NotContain(Stage0GateCheck.Overfitting);
    }

    // 🔴 **否定形**: 「PBO が評価不能」は「PBO が 0（過剰適合なし）」ではない。
    // 判定器へ `Evaluated(0)` を渡した場合と**入力が別物である**ことを固定する
    // （`Evaluated(0)` は下限 20 が効くので不合格になる）。
    [Fact]
    public void 評価不能をPBO0として扱うと判定が変わる_同一視してはならない()
    {
        var notEvaluable = Stage0GateEvaluator.Evaluate(WithoutSearch(), Criteria);
        var asZero = Stage0GateEvaluator.Evaluate(
            WithoutSearch() with { Pbo = new PboVerdict.Evaluated(0d) }, Criteria);

        notEvaluable.Passed.Should().BeTrue();
        asZero.Passed.Should().BeFalse();
        asZero.FailedChecks.Should().Contain(Stage0GateCheck.TrialCount);
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
