using BacktestService.Features.Backtest;
using BacktestService.Features.Backtest.EvaluateStage0Gate;
using BacktestService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace BacktestService.Tests;

// FR-15, FR-20, IADR-0089: Stage 0 判定（Stage0Decision）→ 契約イベント（BacktestEvaluated）の純写像を検証する。
// 発行側（BacktestService）が自分の verdict の契約表現を所有する（バス発行の実駆動は go-live ホスト #82 系）。
//
// FR-20, ADR-0016 決定14, #388, IADR-0304: 「空売りを含む戦略か」は**走行の約定列から観測**する。
// 真偽値で申告する引数は無い（`空売りを含むと申告できる引数が公開面に存在しない` が構造で固定する）。
public class BacktestEvaluatedFactoryTests
{
    private static readonly DateTimeOffset EvaluatedAt = new(2026, 7, 18, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 7, 17);

    private static Stage0Decision Decision(Stage0GateResult gate, double dsr, double pbo, bool cutoff) =>
        new(gate, Stage0Promotion.Evaluate(gate), dsr, pbo, cutoff);

    // 約定列だけを差し替えた走行。エクイティ曲線・指標は本テストの関心事ではないため最小で埋める。
    private static BacktestRun Run(params BacktestFill[] fills) =>
        new([100m, 100m], [], fills, BacktestMetricsCalculator.Compute([100m, 100m], []), 0);

    private static BacktestFill Fill(int signedQuantity, string symbol = "AAPL") =>
        new(Day, symbol, Market.UnitedStates, signedQuantity, 100m, 1m, 0m);

    [Fact]
    public void 合格verdictと実DDを契約イベントへ写す()
    {
        var decision = Decision(new Stage0GateResult(true, []), dsr: 1.23, pbo: 0.10, cutoff: true);

        var e = BacktestEvaluatedFactory.From(
            decision, backtestMaxDrawdownRatio: 0.08m, EvaluatedAt,
            // 売り建て → 買い戻しの往復。空売りを含む走行である。
            Run(Fill(-10), Fill(+10)), strategyId: "short-momentum-v2");

        e.Passed.Should().BeTrue();
        e.MaxDrawdownRatio.Should().Be(0.08m);
        e.DeflatedSharpe.Should().Be(1.23);
        e.ProbabilityOfBacktestOverfitting.Should().Be(0.10);
        e.FailedChecks.Should().BeEmpty();
        e.EvaluatedAt.Should().Be(EvaluatedAt);
        // FR-20, ADR-0016 決定14, #388, IADR-0281 決定3: 空売り実弾解禁の判定入力を運ぶ。
        e.IncludesShortSelling.Should().BeTrue();
        e.StrategyId.Should().Be("short-momentum-v2");
    }

    // FR-20, ADR-0016 決定14, #388, IADR-0281 決定3 / IADR-0304: **否定形** —— 空売りを含まない戦略の合格は
    // `IncludesShortSelling=false` で運ばれる。決定14 は「空売りを**含む**戦略で Stage 0 の 7 条件を
    // 再度満たす」ことを解禁の条件にしており、Passed だけでは解禁の根拠にならない。
    [Fact]
    public void 空売りを含まない戦略の合格はその旨を運ぶ()
    {
        var decision = Decision(new Stage0GateResult(true, []), dsr: 1.23, pbo: 0.10, cutoff: true);

        var e = BacktestEvaluatedFactory.From(
            decision, backtestMaxDrawdownRatio: 0.08m, EvaluatedAt,
            Run(Fill(+10), Fill(-10)), strategyId: "long-only-v1");

        e.Passed.Should().BeTrue();
        e.IncludesShortSelling.Should().BeFalse();
        e.StrategyId.Should().Be("long-only-v1");
    }

    // FR-20, ADR-0016 決定14, #388, IADR-0304 決定1: **否定形（最重要）** ——
    // 「空売りを含む」と**申告する引数が公開面に存在しない**ことを構造で固定する。
    // 機能テストでは守れない要求である——真偽値の引数を足しても、観測値と一致する値を渡す限り
    // 既存のテストは緑のまま通る。**申告できる口が生えたら赤くなる**必要がある。
    [Fact]
    public void 空売りを含むと申告できる引数が公開面に存在しない()
    {
        var parameters = typeof(BacktestEvaluatedFactory)
            .GetMethod(nameof(BacktestEvaluatedFactory.From))!
            .GetParameters();

        // 母集合が空だと否定形が真空的に成立するため、対照（走行を受け取っていること）も併せて見る。
        parameters.Should().NotBeEmpty();
        parameters.Should().Contain(p => p.ParameterType == typeof(BacktestRun));
        parameters.Should().NotContain(p => p.ParameterType == typeof(bool));
    }

    // 戦略識別子が null で渡っても空文字へ倒す（`null` のまま契約へ載せない）。
    // 空文字は「戦略の同一性を名乗れない」状態であり、verdict は StrategyChanged で無効になる（安全側）。
    [Fact]
    public void 戦略識別子がnullなら空文字へ倒す()
    {
        var decision = Decision(new Stage0GateResult(true, []), dsr: 1.23, pbo: 0.10, cutoff: true);

        var e = BacktestEvaluatedFactory.From(decision, 0.08m, EvaluatedAt, Run(Fill(-10)), null!);

        e.StrategyId.Should().BeEmpty();
    }

    [Fact]
    public void 不合格は未達条件を名称で連結して持つ()
    {
        var gate = new Stage0GateResult(false, [Stage0GateCheck.DeflatedSharpe, Stage0GateCheck.MaxDrawdown]);
        var decision = Decision(gate, dsr: 0.5, pbo: 0.7, cutoff: false);

        var e = BacktestEvaluatedFactory.From(
            decision, backtestMaxDrawdownRatio: 0.30m, EvaluatedAt,
            Run(Fill(+10)), strategyId: "baseline-v1");

        e.Passed.Should().BeFalse();
        e.FailedChecks.Should().Contain(nameof(Stage0GateCheck.DeflatedSharpe))
            .And.Contain(nameof(Stage0GateCheck.MaxDrawdown));
    }

    [Fact]
    public void decisionがnullなら例外()
    {
        var act = () => BacktestEvaluatedFactory.From(null!, 0.1m, EvaluatedAt, Run(), "baseline-v1");
        act.Should().Throw<ArgumentNullException>();
    }

    // 走行が渡らなければ観測できない。**false へ倒さず例外**にする——「観測できなかった」を
    // 「空売りを含まない」と読むと、走行を渡し忘れた呼び出しが静かに合格 verdict を作る。
    [Fact]
    public void 走行がnullなら例外()
    {
        var decision = Decision(new Stage0GateResult(true, []), dsr: 1.23, pbo: 0.10, cutoff: true);

        var act = () => BacktestEvaluatedFactory.From(decision, 0.1m, EvaluatedAt, null!, "baseline-v1");
        act.Should().Throw<ArgumentNullException>();
    }
}
