using BacktestService.Domain;
using AwesomeAssertions;
using Xunit;

namespace BacktestService.Tests;

// FR-15, ADR-0008: 試行数の記録（検証条件④の前提）。試行台帳が候補数 N・IS Sharpe の分散・最良候補を提供する。
public class TrialLedgerTests
{
    [Fact]
    public void 空の台帳は件数0_最良なし_分散0()
    {
        var ledger = new TrialLedger();
        ledger.Count.Should().Be(0);
        ledger.BestByInSampleSharpe().Should().BeNull();
        ledger.InSampleSharpeVariance().Should().Be(0d);
    }

    [Fact]
    public void 試行を記録すると件数が増える()
    {
        var ledger = new TrialLedger();
        ledger.Record(new BacktestTrial("a", 1.0, 0.05));
        ledger.Record(new BacktestTrial("b", 2.0, 0.03));
        ledger.Count.Should().Be(2);
    }

    [Fact]
    public void 最良候補はIS_Sharpeが最大のもの()
    {
        var ledger = new TrialLedger();
        ledger.Record(new BacktestTrial("a", 1.0, 0.05));
        ledger.Record(new BacktestTrial("b", 2.0, 0.03));
        ledger.Record(new BacktestTrial("c", 1.5, 0.04));
        ledger.BestByInSampleSharpe()!.Value.Name.Should().Be("b");
    }

    [Fact]
    public void IS_Sharpeの標本分散を算出する()
    {
        var ledger = new TrialLedger();
        ledger.Record(new BacktestTrial("a", 1.0, 0));
        ledger.Record(new BacktestTrial("b", 2.0, 0));
        ledger.Record(new BacktestTrial("c", 1.5, 0));
        // 平均 1.5、偏差 -0.5/0.5/0、二乗和 0.5、/(n-1=2)=0.25。
        ledger.InSampleSharpeVariance().Should().BeApproximately(0.25d, 1e-12);
    }

    // ---- FR-15, ADR-0039 決定1, #1381, IADR-0554: 台帳の IS Sharpe の単位は 1 期間（日次） ----

    private static BacktestMetrics Alternating(decimal up, decimal down)
    {
        var equity = new List<decimal> { 100m };
        for (var i = 1; i <= 60; i++)
            equity.Add(equity[^1] * (1m + (i % 2 == 0 ? up : down)));
        return BacktestMetricsCalculator.Compute(equity, []);
    }

    // 🔴 FromMetrics は判定器（Stage0GateService）が DSR の観測値に使うのと同じ 1 期間 Sharpe を記録する。
    // 変異の対照: `metrics.SharpeRatio`（年率）を記録すると √252 倍になり、この試験が赤になる。
    [Fact]
    public void FromMetricsは判定器の観測値と同じ1期間Sharpeを記録する()
    {
        var metrics = Alternating(0.02m, -0.005m);

        var trial = BacktestTrial.FromMetrics("t", metrics, 0.1);

        var observed = SampleMomentsCalculator.Compute(metrics.DailyReturns).SharpePerPeriod;
        observed.Should().BeGreaterThan(0d);
        trial.InSampleSharpePerPeriod.Should().Be(observed);
        trial.InSampleSharpePerPeriod.Should().BeApproximately(
            metrics.SharpeRatio / Math.Sqrt(BacktestMetricsCalculator.TradingDaysPerYear), 1e-12);
        trial.InSampleSharpePerPeriod.Should().NotBeApproximately(metrics.SharpeRatio, 1e-6);
        trial.OutOfSampleReturn.Should().Be(0.1);
        trial.Name.Should().Be("t");
    }
}
