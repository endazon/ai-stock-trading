using AiStockTrading.Backtest.Domain.Tests.Calibration;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Domain.Tests;

// FR-15, ADR-0008, #208, IADR-0110: 閾値較正に使う決定論的な正規乱数源。
// 較正の数値は根拠として IADR に記録するため、実行のたびに変わってはならない。
// .NET の Random は実装（ランタイム版）によって系列が変わり得るため、較正専用に自前の splitmix64 を用いる。
public class DeterministicNormalSamplerTests
{
    [Fact]
    public void 同じ種は同じ系列を返す_較正の再現性()
    {
        var a = new DeterministicNormalSampler(20260728UL);
        var b = new DeterministicNormalSampler(20260728UL);

        var first = Enumerable.Range(0, 100).Select(_ => a.NextStandardNormal()).ToArray();
        var second = Enumerable.Range(0, 100).Select(_ => b.NextStandardNormal()).ToArray();

        second.Should().Equal(first);
    }

    [Fact]
    public void 異なる種は異なる系列を返す()
    {
        var a = new DeterministicNormalSampler(1UL);
        var b = new DeterministicNormalSampler(2UL);

        a.NextStandardNormal().Should().NotBe(b.NextStandardNormal());
    }

    [Fact]
    public void 標準正規に従う_平均0_標準偏差1()
    {
        // 偽陽性率の較正は「真のエッジが 0」の標本に依存するため、分布の素性を先に固定する。
        var sampler = new DeterministicNormalSampler(42UL);
        var values = Enumerable.Range(0, 200_000).Select(_ => sampler.NextStandardNormal()).ToArray();

        var mean = values.Average();
        var sd = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1));

        mean.Should().BeApproximately(0d, 0.02);
        sd.Should().BeApproximately(1d, 0.02);
    }
}
