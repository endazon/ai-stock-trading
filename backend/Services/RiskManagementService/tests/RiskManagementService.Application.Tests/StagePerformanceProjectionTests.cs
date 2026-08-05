using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Domain;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-20, FR-10, UC-06, ADR-0008, IADR-0103, #164: 実DD（観測最大ドローダウン）の段階別実績への射影（純関数）を検証する。
// 単調非減少・負値の無視・フィールド所有権の分離（他フィールド温存）・観測窓リセットを受け入れ基準へ写像する。
public class StagePerformanceProjectionTests
{
    // 実DD 以外の全フィールドに非既定値を入れた実績（温存の検証用）。
    private static StagePerformance Populated() => new()
    {
        BacktestPassed = true,
        BacktestMaxDrawdownRatio = 0.10m,
        ObservedMaxDrawdownRatio = 0.12m,
        Stage1QualifiedTradingDays = 60,
        Stage1TradeCount = 100,
        SlippageAndCostWithinExpected = true,
        DailyLossLimitRespected = true,
    };

    [Theory]
    // 現在値, 観測値, 期待値
    [InlineData(0.00, 0.05, 0.05)] // 未供給からの初回観測
    [InlineData(0.12, 0.20, 0.20)] // より深い谷を観測 → 更新
    [InlineData(0.20, 0.05, 0.20)] // 回復しても最大値は下がらない
    [InlineData(0.20, 0.20, 0.20)] // 同値
    public void 実DDは単調非減少で更新する(double current, double sampled, double expected)
    {
        // 受け入れ基準 1: 「観測した最大ドローダウン」であり、回復（現在 DD の低下）で下がってはならない。
        var performance = new StagePerformance { ObservedMaxDrawdownRatio = (decimal)current };

        var updated = StagePerformanceProjection.WithObservedDrawdown(performance, (decimal)sampled);

        updated.ObservedMaxDrawdownRatio.Should().Be((decimal)expected);
    }

    [Fact]
    public void 負の観測値は無視する()
    {
        // 受け入れ基準 2: DD 比率は 0 以上。負値（異常な入力）は 0 として扱い、既存の最大値を壊さない。
        var performance = new StagePerformance { ObservedMaxDrawdownRatio = 0.15m };

        StagePerformanceProjection.WithObservedDrawdown(performance, -0.50m)
            .ObservedMaxDrawdownRatio.Should().Be(0.15m);
        StagePerformanceProjection.WithObservedDrawdown(new StagePerformance(), -0.50m)
            .ObservedMaxDrawdownRatio.Should().Be(0m);
    }

    [Fact]
    public void 実DD以外のフィールドは温存する()
    {
        // 受け入れ基準 3（IADR-0089 の鏡像）: 単一行の複数供給源を両立させるため、自分の所有フィールド以外は
        // 上書きしない。特に backtest 由来（BacktestPassed / BacktestMaxDrawdownRatio）を壊さない。
        var updated = StagePerformanceProjection.WithObservedDrawdown(Populated(), 0.30m);

        updated.ObservedMaxDrawdownRatio.Should().Be(0.30m);
        updated.BacktestPassed.Should().BeTrue();
        updated.BacktestMaxDrawdownRatio.Should().Be(0.10m);
        updated.Stage1QualifiedTradingDays.Should().Be(60);
        updated.Stage1TradeCount.Should().Be(100);
        updated.SlippageAndCostWithinExpected.Should().BeTrue();
        updated.DailyLossLimitRespected.Should().BeTrue();
    }

    [Fact]
    public void 観測窓リセットは実DDのみを戻す()
    {
        // 受け入れ基準 4: 差し戻し（再検証のやり直し）で観測窓を区切る。実DD 以外は温存する。
        var reset = StagePerformanceProjection.WithoutObservedDrawdown(Populated());

        reset.ObservedMaxDrawdownRatio.Should().Be(0m);
        reset.BacktestPassed.Should().BeTrue();
        reset.BacktestMaxDrawdownRatio.Should().Be(0.10m);
        reset.Stage1QualifiedTradingDays.Should().Be(60);
        reset.Stage1TradeCount.Should().Be(100);
        reset.SlippageAndCostWithinExpected.Should().BeTrue();
        reset.DailyLossLimitRespected.Should().BeTrue();
    }
}
