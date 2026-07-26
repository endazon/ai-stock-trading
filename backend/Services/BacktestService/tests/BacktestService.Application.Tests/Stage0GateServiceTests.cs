using AiStockTrading.Backtest.Application;
using AiStockTrading.Backtest.Domain;
using AiStockTrading.Configuration.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Application.Tests;

// FR-15, FR-20, ADR-0008, IADR-0045: Stage 0 合格判定オーケストレーション（DSR・PBO・ゲート・昇格推奨の合成）を検証する。
public class Stage0GateServiceTests
{
    // 緩やかに右肩上がり（DD なし・正の Sharpe）のエクイティ曲線を合成する。
    private static BacktestMetrics RisingMetrics()
    {
        var equity = new List<decimal> { 100m };
        for (var i = 1; i <= 252; i++)
        {
            var r = i % 2 == 0 ? 0.01m : 0.02m;
            equity.Add(equity[^1] * (1m + r));
        }
        // 決済損益は勝ち中心（勝率算出用）。
        return BacktestMetricsCalculator.Compute(equity, [5m, 4m, -1m, 3m]);
    }

    private static TrialLedger ThreeTrials()
    {
        var ledger = new TrialLedger();
        ledger.Record(new BacktestTrial("a", 2.9, 0.20));
        ledger.Record(new BacktestTrial("b", 3.0, 0.22));
        ledger.Record(new BacktestTrial("c", 2.8, 0.18));
        return ledger;
    }

    // 支配戦略（PBO=0）の性能行列。
    private static readonly double[][] DominantMatrix =
        [[10, 1], [10, 1], [10, 1], [10, 1]];

    private static PriceBar BarAfterCutoff(int day) =>
        new("AAA", Market.UnitedStates, new DateOnly(2025, 6, day), 10m, 10m, 10m, 10m, 100);

    private static PriceBar BarBeforeCutoff(int day) =>
        new("AAA", Market.UnitedStates, new DateOnly(2024, 6, day), 10m, 10m, 10m, 10m, 100);

    private static readonly DateOnly Cutoff = new(2024, 12, 31);

    [Fact]
    public void 全条件を満たす戦略はStage0合格しStage1昇格を推奨する()
    {
        var context = new Stage0GateContext(
            BaselineMetrics: RisingMetrics(),
            DoubledCostTotalReturn: 0.30m,
            Trials: ThreeTrials(),
            OverfittingPerformanceMatrix: DominantMatrix,
            OverfittingPartitions: 4,
            WalkForwardOutOfSampleReturn: 0.05m,
            Bars: [BarAfterCutoff(2), BarAfterCutoff(3)],
            LlmTrainingCutoff: Cutoff,
            Criteria: Stage0GateCriteria.Default);

        var decision = new Stage0GateService().Evaluate(context);

        decision.DeflatedSharpe.Should().BeGreaterThan(0.95);
        decision.ProbabilityOfBacktestOverfitting.Should().Be(0d);
        decision.DataCutoffSatisfied.Should().BeTrue();
        decision.Gate.Passed.Should().BeTrue();
        decision.Promotion.Recommended.Should().BeTrue();
        decision.Promotion.ToStage.Should().Be(RiskManagement.Domain.TradingStage.Stage1Paper);
    }

    [Fact]
    public void 匿名化済みならカットオフ以前でもデータ健全性を満たす_OR経路()
    {
        // ADR-0008/検証条件①は「カットオフ後 または 匿名化」。匿名化済みならカットオフ以前の日付でも健全とみなす。
        var context = new Stage0GateContext(
            BaselineMetrics: RisingMetrics(),
            DoubledCostTotalReturn: 0.30m,
            Trials: ThreeTrials(),
            OverfittingPerformanceMatrix: DominantMatrix,
            OverfittingPartitions: 4,
            WalkForwardOutOfSampleReturn: 0.05m,
            Bars: [BarBeforeCutoff(2), BarBeforeCutoff(3)], // カットオフ以前だが匿名化済み
            LlmTrainingCutoff: Cutoff,
            Criteria: Stage0GateCriteria.Default,
            DataAnonymized: true);

        var decision = new Stage0GateService().Evaluate(context);

        decision.DataCutoffSatisfied.Should().BeTrue();
        decision.Gate.Passed.Should().BeTrue();
    }

    [Fact]
    public void カットオフ以前のデータを含む戦略は不合格_昇格しない()
    {
        var context = new Stage0GateContext(
            BaselineMetrics: RisingMetrics(),
            DoubledCostTotalReturn: 0.30m,
            Trials: ThreeTrials(),
            OverfittingPerformanceMatrix: DominantMatrix,
            OverfittingPartitions: 4,
            WalkForwardOutOfSampleReturn: 0.05m,
            Bars: [BarBeforeCutoff(2), BarAfterCutoff(3)], // 1 本がカットオフ前
            LlmTrainingCutoff: Cutoff,
            Criteria: Stage0GateCriteria.Default);

        var decision = new Stage0GateService().Evaluate(context);

        decision.DataCutoffSatisfied.Should().BeFalse();
        decision.Gate.Passed.Should().BeFalse();
        decision.Gate.FailedChecks.Should().Contain(Stage0GateCheck.DataCutoff);
        decision.Promotion.Recommended.Should().BeFalse();
    }

    // FR-15, #208, IADR-0105: 実データ源が未供給（既定 no-op）のとき、Stage 0 が従来どおり不合格＝昇格拒否であることを固定する。
    // 実データ源アダプタの追加で fail-safe が緩まないことの回帰テスト（#208 受け入れ基準③）。
    [Fact]
    public async Task 実データ未供給ならStage0は不合格で昇格しない_failsafe()
    {
        var universe = new SecurityUniverse([new UniverseMembership("AAA", Market.UnitedStates, new DateOnly(2025, 1, 1), null)]);

        // 実過去データ源が何も返さない場合（安全既定の no-op・取得全滅のいずれも同じ状態）＝
        // バー 0 本のスナップショット → 空のシミュレーション結果。
        var dataSource = await MaterializedBarDataSource.LoadAsync(
            new EmptyBarSource(), universe, new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        var run = new BacktestRunner(dataSource).Run(new BacktestRequest(
            universe,
            new DateOnly(2025, 1, 1),
            new DateOnly(2025, 12, 31),
            new NoOrderStrategy(),
            new BacktestConfig(1_000m, new BacktestCostModel(TradingAssumptionsDefaults.Create(), SlippageRatio: 0m),
                CostSensitivity.Baseline)));

        var decision = new Stage0GateService().Evaluate(new Stage0GateContext(
            BaselineMetrics: run.Metrics,
            DoubledCostTotalReturn: run.Metrics.TotalReturn,
            Trials: ThreeTrials(),
            OverfittingPerformanceMatrix: DominantMatrix,
            OverfittingPartitions: 4,
            WalkForwardOutOfSampleReturn: 0m,
            Bars: dataSource.GetBars(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)),
            LlmTrainingCutoff: Cutoff,
            Criteria: Stage0GateCriteria.Default));

        decision.Gate.Passed.Should().BeFalse();
        decision.Promotion.Recommended.Should().BeFalse();
        // 標本不足で DSR は 0（保守側）、コスト2倍・ウォークフォワードも正でないため落ちる。
        decision.Gate.FailedChecks.Should().Contain(
            [Stage0GateCheck.DeflatedSharpe, Stage0GateCheck.CostRobustness, Stage0GateCheck.WalkForward]);
        // データカットオフ条件だけは空バーを検出しない（空は真空的に真）。昇格拒否は上記 3 条件が担っている。
        decision.DataCutoffSatisfied.Should().BeTrue();
    }

    private sealed class NoOrderStrategy : IBacktestStrategy
    {
        public IReadOnlyList<BacktestOrder> DecideOrders(BacktestContext context) => [];
    }

    // 実データ未供給の状態（安全既定の no-op アダプタ・全銘柄取得失敗のいずれでも同じ）を表すスタブ。
    private sealed class EmptyBarSource : IHistoricalBarSource
    {
        public Task<HistoricalBarLoad> LoadBarsAsync(
            IReadOnlyList<(string Symbol, Market Market)> symbols,
            DateOnly from,
            DateOnly to,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoricalBarLoad(
                [], [.. symbols.Select(s => new HistoricalBarGap(s.Symbol, s.Market, "実過去データ源が未接続"))]));
    }
}
