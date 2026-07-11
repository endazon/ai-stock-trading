using FluentAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Domain.Tests;

// FR-15, FR-20, ADR-0008: 実弾段階の撤退キルスイッチ（実DD がバックテスト最大DD の 1.5 倍で停止）を検証する。
public class KillSwitchTests
{
    [Fact]
    public void 実DDがバックテスト最大DDの既定1_5倍以上で停止()
    {
        // バックテスト最大DD 10% → 閾値 15%。実DD 15% で停止。
        KillSwitch.ShouldHalt(realizedDrawdown: 0.15m, backtestMaxDrawdown: 0.10m).Should().BeTrue();
    }

    [Fact]
    public void 閾値未満では停止しない()
    {
        KillSwitch.ShouldHalt(realizedDrawdown: 0.149m, backtestMaxDrawdown: 0.10m).Should().BeFalse();
    }

    [Theory]
    [InlineData(0.20, 0.10, 2.0, true)]   // 実20% >= 10%*2=20%
    [InlineData(0.19, 0.10, 2.0, false)]  // 実19% < 20%
    public void 倍率を指定できる(decimal realized, decimal backtestMax, decimal multiple, bool expected)
    {
        KillSwitch.ShouldHalt(realized, backtestMax, multiple).Should().Be(expected);
    }

    [Fact]
    public void バックテスト無ドローダウンでも正の実DDで停止_保守側()
    {
        KillSwitch.ShouldHalt(realizedDrawdown: 0.01m, backtestMaxDrawdown: 0m).Should().BeTrue();
    }

    [Fact]
    public void バックテスト無ドローダウンかつ実DD0なら停止しない()
    {
        KillSwitch.ShouldHalt(realizedDrawdown: 0m, backtestMaxDrawdown: 0m).Should().BeFalse();
    }
}
