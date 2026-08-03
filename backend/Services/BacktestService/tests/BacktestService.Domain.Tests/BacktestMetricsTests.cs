using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Domain.Tests;

// FR-15, ADR-0008: バックテスト結果集計（総リターン・Sharpe・最大DD・勝率・取引数）を検証する。
// Stage 0 合格判定（DSR/最大DD 許容）の入力となる指標。
public class BacktestMetricsTests
{
    [Fact]
    public void 総リターンは初期資金からの増減率()
    {
        var m = BacktestMetricsCalculator.Compute([100m, 110m, 120m], []);
        m.TotalReturn.Should().Be(0.20m); // (120-100)/100
    }

    [Fact]
    public void 最大ドローダウンはピークからの最大下落率()
    {
        // ピーク 110 → 一時 99 まで下落: (110-99)/110 = 0.10。
        var m = BacktestMetricsCalculator.Compute([100m, 110m, 99m, 120m], []);
        m.MaxDrawdown.Should().Be(0.10m);
    }

    [Fact]
    public void 対称なリターンは平均0でSharpe0()
    {
        // 日次リターン +0.10, -0.10 → 平均 0 → Sharpe 0。
        var m = BacktestMetricsCalculator.Compute([100m, 110m, 99m], []);
        m.SharpeRatio.Should().Be(0d);
    }

    [Fact]
    public void 分散0の一定リターンはSharpe0_ゼロ除算回避()
    {
        // 日次リターン +0.10, +0.10 → 標準偏差 0 → Sharpe 0（ゼロ除算しない）。
        var m = BacktestMetricsCalculator.Compute([100m, 110m, 121m], []);
        m.SharpeRatio.Should().Be(0d);
    }

    [Fact]
    public void Sharpeは日次超過リターンを標本標準偏差で割り年率化する()
    {
        // 日次リターン +0.20, +0.10 → 平均 0.15、標本SD sqrt(0.005)=0.0707107、
        // 日次Sharpe 0.15/0.0707107=2.12132、年率化 ×sqrt(252)=15.8745 → 33.676。
        var m = BacktestMetricsCalculator.Compute([100m, 120m, 132m], []);
        m.SharpeRatio.Should().BeApproximately(33.676d, 0.01d);
    }

    [Fact]
    public void 勝率と取引数は決済損益から算出()
    {
        var m = BacktestMetricsCalculator.Compute([100m, 100m], [5m, -3m, 2m, -1m]);
        m.TradeCount.Should().Be(4);
        m.WinRate.Should().Be(0.5m); // 4 件中 2 件が正
    }

    [Fact]
    public void 空のエクイティ曲線は全て0()
    {
        var m = BacktestMetricsCalculator.Compute([], []);
        m.TotalReturn.Should().Be(0m);
        m.SharpeRatio.Should().Be(0d);
        m.MaxDrawdown.Should().Be(0m);
        m.TradeCount.Should().Be(0);
    }

    [Fact]
    public void 日次リターン列を公開する_後段のDSR入力()
    {
        var m = BacktestMetricsCalculator.Compute([100m, 110m, 99m], []);
        m.DailyReturns.Should().HaveCount(2);
        m.DailyReturns[0].Should().BeApproximately(0.10d, 1e-9);
    }
}
