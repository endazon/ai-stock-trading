using FluentAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Domain.Tests;

// FR-15, IADR-0038/0039: リターン列の標本モーメント（平均・標本SD・歪度・尖度・1期間Sharpe）。DSR の入力。
public class SampleMomentsTests
{
    [Fact]
    public void 一期間Sharpeは平均を標本標準偏差で割る()
    {
        // [1,2] → 平均 1.5、標本SD sqrt(0.5)=0.707107、Sharpe 2.12132。
        var m = SampleMomentsCalculator.Compute([1d, 2d]);
        m.SharpePerPeriod.Should().BeApproximately(2.12132, 1e-4);
    }

    [Fact]
    public void 対称データは歪度0()
    {
        var m = SampleMomentsCalculator.Compute([-2d, -1d, 0d, 1d, 2d]);
        m.Skewness.Should().BeApproximately(0d, 1e-12);
    }

    [Fact]
    public void 右に裾を引くデータは歪度が正()
    {
        var m = SampleMomentsCalculator.Compute([0d, 0d, 0d, 0d, 10d]);
        m.Skewness.Should().BeGreaterThan(0d);
    }

    [Fact]
    public void 尖度は標準化4次モーメント_非超過()
    {
        // [0,0,0,0,10] → 母集団分散16、4次平均832、尖度 832/256=3.25。
        var m = SampleMomentsCalculator.Compute([0d, 0d, 0d, 0d, 10d]);
        m.Kurtosis.Should().BeApproximately(3.25d, 1e-9);
    }

    [Fact]
    public void 一定データは標準偏差0でSharpe0_ゼロ除算回避()
    {
        var m = SampleMomentsCalculator.Compute([5d, 5d, 5d]);
        m.SampleStandardDeviation.Should().Be(0d);
        m.SharpePerPeriod.Should().Be(0d);
    }

    [Fact]
    public void 空列は件数0()
    {
        var m = SampleMomentsCalculator.Compute([]);
        m.Count.Should().Be(0);
        m.SharpePerPeriod.Should().Be(0d);
    }
}
