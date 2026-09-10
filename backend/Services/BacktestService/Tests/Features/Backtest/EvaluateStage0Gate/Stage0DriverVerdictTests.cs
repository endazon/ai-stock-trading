using BacktestService.Domain;
using BacktestService.Features.Backtest.EvaluateStage0Gate;
using AwesomeAssertions;
using Xunit;

namespace BacktestService.Tests;

// FR-15, FR-20, ADR-0008, #688, IADR-0310: 駆動が発行する verdict は**不合格固定**であることを固定する。
// 統制系のため 3 点セット（境界値テーブル・プロパティベース・否定形）で書く。
public class Stage0DriverVerdictTests
{
    // 境界値テーブル: どの入口・どの入力でも Passed は false であり、理由が必ず読める。
    public static TheoryData<string, Stage0Decision> AllEntryPoints() => new()
    {
        { "バー0本", Stage0DriverVerdict.NoHistoricalBars() },
        { "プレースホルダ走行・カットオフ充足", Stage0DriverVerdict.PlaceholderRun(dataCutoffSatisfied: true) },
        { "プレースホルダ走行・カットオフ未充足", Stage0DriverVerdict.PlaceholderRun(dataCutoffSatisfied: false) },
    };

    [Theory]
    [MemberData(nameof(AllEntryPoints))]
    public void どの入口でも不合格で理由が空でない(string label, Stage0Decision decision)
    {
        // FR-15, FR-20, ADR-0008: 合格 verdict は本型からは出ない（合格を出せるのは Stage0GateService の 7 条件だけ）。
        decision.Gate.Passed.Should().BeFalse(label);
        decision.Gate.FailedChecks.Should().NotBeEmpty(label);
        decision.Gate.FormatFailedChecks().Should().NotBeNullOrWhiteSpace(label);
        // 昇格推奨も出ない（据え置き）。
        decision.Promotion.Recommended.Should().BeFalse(label);
        decision.Promotion.ToStage.Should().BeNull(label);
    }

    // **プロパティベース**: カットオフ充足の真偽にかかわらず、プレースホルダの verdict は
    // 必ず PlaceholderStrategy を理由に含み、決して合格しない（不変条件）。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void プレースホルダ走行はカットオフ充足に関わらず不合格でプレースホルダ理由を含む(bool cutoffSatisfied)
    {
        // FR-15, FR-20, ADR-0033, ADR-0008: プレースホルダの成績を本番の合否として記録させない。
        var decision = Stage0DriverVerdict.PlaceholderRun(cutoffSatisfied);

        decision.Gate.Passed.Should().BeFalse();
        decision.Gate.FailedChecks.Should().Contain(Stage0GateCheck.PlaceholderStrategy);
        decision.DataCutoffSatisfied.Should().Be(cutoffSatisfied);
    }

    [Fact]
    public void カットオフ未充足なら理由にデータカットオフも載る()
    {
        // ADR-0033 決定3: カットオフ日が未構成のときも「未充足」として渡ってくる（合格側へ倒さない）。
        var decision = Stage0DriverVerdict.PlaceholderRun(dataCutoffSatisfied: false);

        decision.Gate.FailedChecks.Should().Contain(Stage0GateCheck.DataCutoff);
    }

    [Fact]
    public void カットオフ充足なら理由はプレースホルダのみ()
    {
        // 境界: 充足している条件を未達として載せない（理由の読み違えを作らない）。
        var decision = Stage0DriverVerdict.PlaceholderRun(dataCutoffSatisfied: true);

        decision.Gate.FailedChecks.Should().ContainSingle()
            .Which.Should().Be(Stage0GateCheck.PlaceholderStrategy);
    }

    // **否定形（最重要）**: 過去データが空のとき、判定器の 7 条件では止められない。
    [Fact]
    public void バー0本の理由は判定器の条件では出ない値である_空を検出できない穴を塞ぐ()
    {
        // FR-15, #688, IADR-0310 決定2: DataCutoffPolicy は空バーを違反と見なさない（真空的に真）。
        // つまり「空データでも検証条件①は満たしているように見える」。駆動側が明示的に弾く。
        DataCutoffPolicy.IsAllAfterCutoff([], new DateOnly(2030, 1, 1)).Should().BeTrue();

        var decision = Stage0DriverVerdict.NoHistoricalBars();

        decision.Gate.Passed.Should().BeFalse();
        decision.Gate.FailedChecks.Should().Contain(Stage0GateCheck.NoHistoricalBars);
        // 空データでは「カットオフを満たした」と名乗らない（DataCutoffSatisfied は false）。
        decision.DataCutoffSatisfied.Should().BeFalse();
    }

    // **否定形**: 判定器（Stage0GateEvaluator）は駆動側の 2 値を決して出さない（判定器の 7 条件は不変）。
    [Fact]
    public void 判定器は駆動側の理由を出さない()
    {
        // FR-15, ADR-0008: 7 条件すべてが未達になる入力を与えても、出るのは 7 条件だけである。
        var evaluation = new Stage0GateEvaluation(
            DeflatedSharpe: 0d,
            ProbabilityOfBacktestOverfitting: 1d,
            MaxDrawdown: 1m,
            DoubledCostTotalReturn: -1m,
            WalkForwardOutOfSampleReturn: -1m,
            TrialCount: 0,
            DataCutoffSatisfied: false);

        var result = Stage0GateEvaluator.Evaluate(evaluation, Stage0GateCriteria.Default);

        result.FailedChecks.Should().NotContain(Stage0GateCheck.NoHistoricalBars)
            .And.NotContain(Stage0GateCheck.PlaceholderStrategy);
    }
}
