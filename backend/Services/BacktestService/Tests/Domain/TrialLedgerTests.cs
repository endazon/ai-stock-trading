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
}
