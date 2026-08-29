using BacktestService.Features.Backtest;
using BacktestService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Xunit;

namespace BacktestService.Tests;

// FR-15, FR-20, IADR-0089: Stage 0 判定（Stage0Decision）→ 契約イベント（BacktestEvaluated）の純写像を検証する。
// 発行側（BacktestService）が自分の verdict の契約表現を所有する（バス発行の実駆動は go-live ホスト #82 系）。
public class BacktestEvaluatedFactoryTests
{
    private static readonly DateTimeOffset EvaluatedAt = new(2026, 7, 18, 9, 0, 0, TimeSpan.Zero);

    private static Stage0Decision Decision(Stage0GateResult gate, double dsr, double pbo, bool cutoff) =>
        new(gate, Stage0Promotion.Evaluate(gate), dsr, pbo, cutoff);

    [Fact]
    public void 合格verdictと実DDを契約イベントへ写す()
    {
        var decision = Decision(new Stage0GateResult(true, []), dsr: 1.23, pbo: 0.10, cutoff: true);

        var e = BacktestEvaluatedFactory.From(decision, backtestMaxDrawdownRatio: 0.08m, EvaluatedAt);

        e.Passed.Should().BeTrue();
        e.MaxDrawdownRatio.Should().Be(0.08m);
        e.DeflatedSharpe.Should().Be(1.23);
        e.ProbabilityOfBacktestOverfitting.Should().Be(0.10);
        e.FailedChecks.Should().BeEmpty();
        e.EvaluatedAt.Should().Be(EvaluatedAt);
    }

    [Fact]
    public void 不合格は未達条件を名称で連結して持つ()
    {
        var gate = new Stage0GateResult(false, [Stage0GateCheck.DeflatedSharpe, Stage0GateCheck.MaxDrawdown]);
        var decision = Decision(gate, dsr: 0.5, pbo: 0.7, cutoff: false);

        var e = BacktestEvaluatedFactory.From(decision, backtestMaxDrawdownRatio: 0.30m, EvaluatedAt);

        e.Passed.Should().BeFalse();
        e.FailedChecks.Should().Contain(nameof(Stage0GateCheck.DeflatedSharpe))
            .And.Contain(nameof(Stage0GateCheck.MaxDrawdown));
    }

    [Fact]
    public void decisionがnullなら例外()
    {
        var act = () => BacktestEvaluatedFactory.From(null!, 0.1m, EvaluatedAt);
        act.Should().Throw<ArgumentNullException>();
    }
}
