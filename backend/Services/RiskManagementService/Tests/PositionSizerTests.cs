using RiskManagementService.Domain;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, ADR-0018: 1取引あたりリスク（既定: 資金の 1%・ATR 連動サイジング）と連敗時縮小（既定: 5 連敗で半減）
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
    // FR-10, #329, ADR-0018: 確定単一値は **5 連敗**（旧レンジ「3〜5」の保守側 3 からの是正）。
    // 境界値: 4（直下・縮小しない）/ 5（一致・半減）/ 6（直上・半減）。
    [InlineData(0, 1.0)]
    [InlineData(4, 1.0)]
    [InlineData(5, 0.5)]
    [InlineData(6, 0.5)]
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

        var factor = PositionSizer.GetSizeFactor(consecutiveLosses: 5, drawdownRatio: 0.05m, limits);

        factor.Should().Be(0.25m);
    }

    [Fact]
    public void リスク予算基準が金額上限内ならキャップされない()
    {
        // Issue #29: 損切り幅が十分深くリスク予算基準の株数が金額上限内に収まるケース。
        // 資金 100,000 × 1% = 1,000 円 ÷ 損切り 30 円 = 33 株。想定金額 33 × 100 = 3,300 円 ≦ 上限 35,000 円。
        var quantity = PositionSizer.CalculateCappedQuantity(
            capital: 100_000m, perTradeRiskRatio: 0.01m, stopLossDistancePerShare: 30m,
            referencePrice: 100m, maxOrderAmount: 35_000m, availableCapital: 100_000m);

        quantity.Should().Be(33);
    }

    [Fact]
    public void 損切り幅が浅い場合は1注文金額上限で株数がキャップされる()
    {
        // Issue #29: 損切り幅 10 円（浅い）→ リスク予算基準 1,000 ÷ 10 = 100 株（想定金額 100,000 円）。
        // 1 注文金額上限 35,000 円 ÷ 参照価格 1,000 円 = 35 株にキャップされ、想定金額は上限内に収まる。
        var quantity = PositionSizer.CalculateCappedQuantity(
            capital: 100_000m, perTradeRiskRatio: 0.01m, stopLossDistancePerShare: 10m,
            referencePrice: 1_000m, maxOrderAmount: 35_000m, availableCapital: 100_000m);

        quantity.Should().Be(35);
        (quantity * 1_000m).Should().BeLessThanOrEqualTo(35_000m);
    }

    [Fact]
    public void 利用可能資金が金額上限より小さい場合は資金でキャップされる()
    {
        // Issue #29: 段階資金上限の残枠など、利用可能資金が 1 注文金額上限より小さいときは資金基準でキャップ。
        // 利用可能資金 20,000 円 ÷ 参照価格 1,000 円 = 20 株。
        var quantity = PositionSizer.CalculateCappedQuantity(
            capital: 100_000m, perTradeRiskRatio: 0.01m, stopLossDistancePerShare: 10m,
            referencePrice: 1_000m, maxOrderAmount: 35_000m, availableCapital: 20_000m);

        quantity.Should().Be(20);
    }

    [Fact]
    public void 参照価格がゼロ以下なら見送りとして株数ゼロを返す()
    {
        // Issue #29: 金額基準のキャップは参照価格で割るため、価格が正でない注文は見送り（0 株）。
        PositionSizer.CalculateCappedQuantity(100_000m, 0.01m, 10m, 0m, 35_000m, 100_000m).Should().Be(0);
        PositionSizer.CalculateCappedQuantity(100_000m, 0.01m, 10m, -1m, 35_000m, 100_000m).Should().Be(0);
    }

    [Fact]
    public void 損切り幅がゼロ以下ならキャップ版も見送りとして株数ゼロを返す()
    {
        // Issue #29: キャップ版でも損切り幅が正でなければリスク予算基準が 0 となり、min で 0 株（見送り）。
        PositionSizer.CalculateCappedQuantity(100_000m, 0.01m, 0m, 1_000m, 35_000m, 100_000m).Should().Be(0);
        PositionSizer.CalculateCappedQuantity(100_000m, 0.01m, -5m, 1_000m, 35_000m, 100_000m).Should().Be(0);
    }
}
