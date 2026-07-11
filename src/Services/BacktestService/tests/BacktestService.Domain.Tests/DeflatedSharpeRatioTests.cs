using FluentAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Domain.Tests;

// FR-15, ADR-0008, 06_daytrading-review §3.2, IADR-0044: Deflated Sharpe Ratio。
// 多重検定（試行数）と非正規性を補正した「真 Sharpe が正である確率」を検証する。
public class DeflatedSharpeRatioTests
{
    [Fact]
    public void 期待最大Sharpeは試行数が増えるほど大きい()
    {
        var few = DeflatedSharpeRatio.ExpectedMaxSharpe(0.04, trials: 5);
        var many = DeflatedSharpeRatio.ExpectedMaxSharpe(0.04, trials: 50);
        many.Should().BeGreaterThan(few);
    }

    [Fact]
    public void 期待最大Sharpeは試行Sharpe標準偏差に比例する()
    {
        // sqrt(V) が 2 倍 → 期待最大 Sharpe も 2 倍（括弧内は N のみ依存）。
        var low = DeflatedSharpeRatio.ExpectedMaxSharpe(0.01, trials: 10);  // std 0.1
        var high = DeflatedSharpeRatio.ExpectedMaxSharpe(0.04, trials: 10); // std 0.2
        (high / low).Should().BeApproximately(2.0, 1e-9);
    }

    [Fact]
    public void 分散0や試行2未満の期待最大Sharpeは0()
    {
        DeflatedSharpeRatio.ExpectedMaxSharpe(0.0, 10).Should().Be(0d);
        DeflatedSharpeRatio.ExpectedMaxSharpe(0.04, 1).Should().Be(0d);
    }

    [Fact]
    public void 観測Sharpeが期待最大と等しいときDSRは0_5()
    {
        DeflatedSharpeRatio.Compute(observedSharpe: 0.15, sampleLength: 100, skew: 0, kurtosis: 3, expectedMaxSharpe: 0.15)
            .Should().BeApproximately(0.5, 1e-6);
    }

    [Fact]
    public void 観測Sharpeが期待最大を上回るとDSRは0_5超()
    {
        DeflatedSharpeRatio.Compute(0.2, 100, 0, 3, 0.1).Should().BeGreaterThan(0.5);
    }

    [Fact]
    public void 具体値_正規かつT101_SR0_2_SR0_0_1()
    {
        // 分子 (0.2-0.1)*sqrt(100)=1.0、分母 sqrt(1+(3-1)/4*0.04)=sqrt(1.02)=1.00995、
        // z=0.990148、DSR=Φ(z)≈0.83896。
        DeflatedSharpeRatio.Compute(0.2, 101, 0, 3, 0.1).Should().BeApproximately(0.839, 2e-3);
    }

    [Fact]
    public void 標本が長いほどDSRは1に近づく_エッジ確信度()
    {
        var shortSample = DeflatedSharpeRatio.Compute(0.2, 50, 0, 3, 0.1);
        var longSample = DeflatedSharpeRatio.Compute(0.2, 500, 0, 3, 0.1);
        longSample.Should().BeGreaterThan(shortSample);
    }

    [Fact]
    public void 標本長2未満は0_保守側()
    {
        DeflatedSharpeRatio.Compute(0.2, 1, 0, 3, 0.1).Should().Be(0d);
    }
}
