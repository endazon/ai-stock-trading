using FluentAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Domain.Tests;

// FR-15, IADR-0044: DSR に必要な標準正規 CDF / 逆 CDF（外部依存なしの純実装）を検証する。
public class NormalDistributionTests
{
    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(1.281551566, 0.9)]
    [InlineData(1.959963985, 0.975)]
    [InlineData(-1.959963985, 0.025)]
    public void 標準正規CDF(double z, double expected)
    {
        NormalDistribution.Cdf(z).Should().BeApproximately(expected, 1e-4);
    }

    [Theory]
    [InlineData(0.5, 0.0)]
    [InlineData(0.9, 1.281551566)]
    [InlineData(0.975, 1.959963985)]
    [InlineData(0.025, -1.959963985)]
    public void 標準正規逆CDF(double p, double expected)
    {
        NormalDistribution.InverseCdf(p).Should().BeApproximately(expected, 1e-4);
    }

    [Fact]
    public void CDFと逆CDFは往復する()
    {
        NormalDistribution.Cdf(NormalDistribution.InverseCdf(0.83)).Should().BeApproximately(0.83, 1e-6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.5)]
    public void 逆CDFの定義域外は例外(double p)
    {
        var act = () => NormalDistribution.InverseCdf(p);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
