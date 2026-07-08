using AiStockTrading.RiskManagement.Domain;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Domain.Tests;

// FR-10: 1取引あたりリスク（既定: 資金の0.5〜1%、ATR連動サイジング）と連敗時縮小
public class PositionSizerTests
{
    [Fact]
    public void リスク予算と損切り幅から株数を算出する()
    {
        // 資金 100,000 × リスク 1% = 1,000 円。損切り幅 30 円/株 → 33 株
        var quantity = PositionSizer.CalculateQuantity(
            capital: 100_000m, perTradeRiskRatio: 0.01m, stopLossDistancePerShare: 30m);

        quantity.Should().Be(33);
    }

    [Fact]
    public void 損切り幅がゼロ以下なら見送りとして株数ゼロを返す()
    {
        PositionSizer.CalculateQuantity(100_000m, 0.01m, 0m).Should().Be(0);
        PositionSizer.CalculateQuantity(100_000m, 0.01m, -1m).Should().Be(0);
    }

    [Fact]
    public void 縮小係数がサイズに乗算される()
    {
        // 連敗時縮小: 係数 0.5 → 1,000 × 0.5 = 500 円 ÷ 30 円 = 16 株
        var quantity = PositionSizer.CalculateQuantity(
            100_000m, 0.01m, 30m, sizeFactor: 0.5m);

        quantity.Should().Be(16);
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(2, 1.0)]
    [InlineData(3, 0.5)] // 既定: 3連敗でサイズ半減
    [InlineData(5, 0.5)]
    public void 連敗数に応じた縮小係数を返す(int consecutiveLosses, decimal expectedFactor)
    {
        var limits = TradingDefaults.CreateRiskLimits();

        var factor = PositionSizer.GetSizeFactor(consecutiveLosses, drawdownRatio: 0m, limits);

        factor.Should().Be(expectedFactor);
    }

    [Fact]
    public void ドローダウンが上限の半分に達したらサイズを半減する()
    {
        // 全体前提条件: DD が深まるほどサイズを縮小（決定的ルール: 上限の 1/2 到達で半減）
        var limits = TradingDefaults.CreateRiskLimits(); // MaxDrawdownRatio = 0.10

        var factor = PositionSizer.GetSizeFactor(consecutiveLosses: 0, drawdownRatio: 0.05m, limits);

        factor.Should().Be(0.5m);
    }

    [Fact]
    public void 連敗とドローダウンの縮小は重畳する()
    {
        var limits = TradingDefaults.CreateRiskLimits();

        var factor = PositionSizer.GetSizeFactor(consecutiveLosses: 3, drawdownRatio: 0.05m, limits);

        factor.Should().Be(0.25m);
    }
}
