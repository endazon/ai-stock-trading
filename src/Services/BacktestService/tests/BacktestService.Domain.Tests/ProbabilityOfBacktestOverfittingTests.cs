using FluentAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Domain.Tests;

// FR-15, ADR-0008, 06_daytrading-review §3.2, IADR-0044: Probability of Backtest Overfitting（CSCV）。
// 「IS で最良の戦略が OOS で中央値以下に落ちる確率」を検証する。performance[block][strategy]。
public class ProbabilityOfBacktestOverfittingTests
{
    [Fact]
    public void 支配戦略はPBO0_IS最良がOOSでも最良()
    {
        // 戦略0 が全ブロックで最良 → どの分割でも IS 最良=戦略0、OOS でも最良 → 過剰適合なし。
        double[][] perf =
        [
            [10, 1], [10, 1], [10, 1], [10, 1],
        ];
        ProbabilityOfBacktestOverfitting.Compute(perf, partitions: 4).Should().Be(0d);
    }

    [Fact]
    public void 交互に優劣が入れ替わる構成はPBO1_過剰適合()
    {
        // 戦略A が前半ブロック・戦略B が後半ブロックで優位 → IS 最良は OOS で最悪 → 全分割で過剰適合。
        double[][] perf =
        [
            [10, 0], [9, 1], [0, 10], [1, 9],
        ];
        ProbabilityOfBacktestOverfitting.Compute(perf, partitions: 4).Should().Be(1d);
    }

    [Fact]
    public void 決定的_同じ入力は同じPBO()
    {
        double[][] perf = [[3, 1], [1, 3], [2, 2], [1, 2]];
        var a = ProbabilityOfBacktestOverfitting.Compute(perf, 4);
        var b = ProbabilityOfBacktestOverfitting.Compute(perf, 4);
        a.Should().Be(b);
        a.Should().BeInRange(0d, 1d);
    }

    [Theory]
    [InlineData(3)]  // 奇数
    [InlineData(1)]  // 2 未満
    public void 分割数が偶数かつ2以上でなければ例外(int partitions)
    {
        double[][] perf = [[1, 2], [2, 1]];
        var act = () => ProbabilityOfBacktestOverfitting.Compute(perf, partitions);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 戦略が2未満なら例外()
    {
        double[][] perf = [[1], [2], [3], [4]];
        var act = () => ProbabilityOfBacktestOverfitting.Compute(perf, 4);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 行の長さが不揃いなら例外_一貫した入力検証()
    {
        // ブロック行の戦略数が不揃い（jagged）→ 他の検証と同じく ArgumentException（#100 レビュー指摘）。
        double[][] perf = [[1, 2], [1, 2, 3], [2, 1], [1, 2]];
        var act = () => ProbabilityOfBacktestOverfitting.Compute(perf, 4);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 先頭行がnullでも素のNRESではなくArgumentException_一貫した入力検証()
    {
        // 先頭ブロック行が null → strategyCount 算出前にガードし NullReferenceException を出さない（#100 レビュー持ち越し指摘）。
        double[]?[] perf = [null, [1, 2], [2, 1], [1, 2]];
        var act = () => ProbabilityOfBacktestOverfitting.Compute(perf!, 4);
        act.Should().Throw<ArgumentException>();
    }
}
