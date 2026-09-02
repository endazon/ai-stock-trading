using MarketMonitorService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace MarketMonitorService.Tests;

// FR-03, FR-10, ADR-0003: 損切りライン到達判定（建玉方向で対称）の検証。
public class StopLossEvaluatorTests
{
    private static HeldPosition Long(decimal stop) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, stop);

    private static HeldPosition Short(decimal stop) =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, 10, 1_000m, stop);

    [Fact]
    public void ロングは現在値が損切り価格以下で到達()
    {
        StopLossEvaluator.IsTriggered(Long(970m), 970m).Should().BeTrue();   // ちょうど
        StopLossEvaluator.IsTriggered(Long(970m), 965m).Should().BeTrue();   // 割れ
    }

    [Fact]
    public void ロングは損切り価格より上なら未到達()
    {
        StopLossEvaluator.IsTriggered(Long(970m), 975m).Should().BeFalse();
    }

    [Fact]
    public void ショートは現在値が損切り価格以上で到達()
    {
        StopLossEvaluator.IsTriggered(Short(1_030m), 1_030m).Should().BeTrue(); // ちょうど
        StopLossEvaluator.IsTriggered(Short(1_030m), 1_040m).Should().BeTrue(); // 上抜け
    }

    [Fact]
    public void ショートは損切り価格より下なら未到達()
    {
        StopLossEvaluator.IsTriggered(Short(1_030m), 1_025m).Should().BeFalse();
    }
}
