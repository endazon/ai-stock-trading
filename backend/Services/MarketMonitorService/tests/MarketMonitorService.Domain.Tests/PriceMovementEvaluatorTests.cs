using AiStockTrading.MarketMonitor.Domain;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.MarketMonitor.Domain.Tests;

// FR-03, UC-02: 変動率判定（基準値比・閾値超過）の検証。
public class PriceMovementEvaluatorTests
{
    [Fact]
    public void 上昇が閾値を超えたら超過と判定する()
    {
        // 基準 1000 → 現在 1031（+3.1%）、閾値 3%。
        var m = PriceMovementEvaluator.Evaluate(1031m, 1000m, 0.03m);

        m.Exceeded.Should().BeTrue();
        m.ChangeRatio.Should().BeApproximately(0.031m, 0.0001m);
    }

    [Fact]
    public void 下落が閾値を超えたら超過と判定する()
    {
        // 基準 1000 → 現在 960（-4%）、閾値 3%。
        var m = PriceMovementEvaluator.Evaluate(960m, 1000m, 0.03m);

        m.Exceeded.Should().BeTrue();
        m.ChangeRatio.Should().BeApproximately(-0.04m, 0.0001m);
    }

    [Fact]
    public void 閾値未満の変動は超過としない()
    {
        // +2% < 閾値 3%。
        var m = PriceMovementEvaluator.Evaluate(1020m, 1000m, 0.03m);

        m.Exceeded.Should().BeFalse();
    }

    [Fact]
    public void 閾値ちょうどは超過とする()
    {
        // ちょうど 3%（境界は超過側に含める）。
        var m = PriceMovementEvaluator.Evaluate(1030m, 1000m, 0.03m);

        m.Exceeded.Should().BeTrue();
    }

    [Fact]
    public void 基準値がゼロ以下なら超過としない()
    {
        // 基準値未確定・異常は誤発火を避けて超過扱いにしない。
        PriceMovementEvaluator.Evaluate(1000m, 0m, 0.03m).Exceeded.Should().BeFalse();
        PriceMovementEvaluator.Evaluate(1000m, -5m, 0.03m).Exceeded.Should().BeFalse();
    }
}
