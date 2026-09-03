using CostControlService.Domain;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace CostControlService.Tests;

// NFR（費用）, 05_trading-assumptions §6, IADR-0027: LLM 上限に対する統制判定（80%間隔延長・100%停止）を検証する。
public class CostGovernorTests
{
    // LLM 上限 15,000 円。
    private static readonly MonthlyCostLimits Limits = new(20_000m, 15_000m, 5_000m, 0m);

    [Theory]
    [InlineData(0, CostControlState.Normal)]
    [InlineData(11_999, CostControlState.Normal)]   // <80%
    [InlineData(12_000, CostControlState.Throttled)] // =80%
    [InlineData(14_999, CostControlState.Throttled)] // <100%
    [InlineData(15_000, CostControlState.Halted)]    // =100%
    [InlineData(20_000, CostControlState.Halted)]    // >100%
    public void しきい値で状態が切り替わる(decimal cost, CostControlState expected)
    {
        CostGovernor.EvaluateLlm(cost, Limits).State.Should().Be(expected);
    }

    [Fact]
    public void Throttled_は間隔倍率2_Normal_は1()
    {
        CostGovernor.EvaluateLlm(0m, Limits).IntervalMultiplier.Should().Be(1m);
        CostGovernor.EvaluateLlm(13_000m, Limits).IntervalMultiplier.Should().Be(2m);
    }

    [Fact]
    public void 上限が0なら統制しない_Normal()
    {
        var noLimit = new MonthlyCostLimits(0m, 0m, 0m, 0m);
        CostGovernor.EvaluateLlm(999_999m, noLimit).State.Should().Be(CostControlState.Normal);
    }

    [Fact]
    public void Halted_は停止フラグが立つ()
    {
        CostGovernor.EvaluateLlm(15_000m, Limits).IsHalted.Should().BeTrue();
    }

    [Fact]
    public void 費用対資金比率を算出する()
    {
        CostReview.CostToCapitalRatio(20_000m, 100_000m).Should().Be(0.2m);
        CostReview.CostToCapitalRatio(1_000m, 0m).Should().Be(0m); // 資金0は0
    }
}
