using RiskManagementService.Domain;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, ADR-0018: 1取引あたりリスク（既定: 資金の 1%。損切り幅は入力として受け取る＝取引判断が下限を掛けた幅。IADR-0465）と
// 連敗時縮小（既定: 5 連敗で半減）
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
    // T-10-2370, FR-10, #1174, IADR-0500 決定1: 投入可能な資金が参照価格 × 1 株に満たないか（LLM を呼ぶ前の見送りの下界）。
    // ちょうど等しい（1 株ちょうど買える）は偽、1 セント足りなければ真。資金 0 以下は真。参照価格が正でなければ偽（下界として何も言えない）。
    [Theory]
    [InlineData("2500", "2500", false)]
    [InlineData("2499.99", "2500", true)]
    [InlineData("2500.01", "2500", false)]
    [InlineData("2000", "2500", true)]
    [InlineData("0", "2500", true)]
    [InlineData("-1", "2500", true)]
    [InlineData("100", "0", false)]
    [InlineData("100", "-1", false)]
    public void T_10_2370_投入可能な資金が1株の価格に満たないかを判定する(string availableText, string priceText, bool expected)
    {
        var available = decimal.Parse(availableText, System.Globalization.CultureInfo.InvariantCulture);
        var price = decimal.Parse(priceText, System.Globalization.CultureInfo.InvariantCulture);

        PositionSizer.CannotAffordOneShare(available, price).Should().Be(expected);
    }

    // T-10-2370: 🔴 **下界はサイジングと一致する**（LLM の前に省いてよい根拠）。下界が真なら、損切り幅（LLM 依存）・1 注文上限・縮小係数を
    // どう選んでもサイジングの数量は 0。偽なら、リスク予算と 1 注文上限が十分なとき残枠の金額キャップで 1 株以上買える（＝省くと結論が変わる）。
    // 日本株（JPY）の換算後の価格（端数のある基準通貨）も含める。
    [Theory]
    [InlineData("2000", "2500")]
    [InlineData("2500", "2500")]
    [InlineData("2499.99", "2500")]
    [InlineData("1999.9936", "1999.9936")]
    [InlineData("1999.99", "1999.9936")]
    [InlineData("0", "334.11")]
    [InlineData("334.11", "334.11")]
    [InlineData("334.10", "334.11")]
    public void T_10_2370_下界が真ならどの損切り幅と上限でもサイジングは0株で偽なら1株以上(string availableText, string priceText)
    {
        var available = decimal.Parse(availableText, System.Globalization.CultureInfo.InvariantCulture);
        var price = decimal.Parse(priceText, System.Globalization.CultureInfo.InvariantCulture);
        var cannot = PositionSizer.CannotAffordOneShare(available, price);

        foreach (var stop in new[] { 0.01m, 1m, 50m })
        {
            foreach (var maxOrder in new[] { 1_000m, 25_000m, 1_000_000m })
            {
                foreach (var factor in new[] { 1m, 0.5m, 0.25m })
                {
                    var quantity = PositionSizer.CalculateCappedQuantity(
                        capital: 1_000_000m, perTradeRiskRatio: 0.01m, stopLossDistancePerShare: stop,
                        referencePrice: price, maxOrderAmount: maxOrder, availableCapital: available, sizeFactor: factor);
                    if (cannot)
                    {
                        quantity.Should().Be(0, "下界が真なら結論（損切り幅）に依らず数量 0（available={0} price={1}）", available, price);
                    }
                }
            }
        }

        if (!cannot)
        {
            PositionSizer.CalculateCappedQuantity(
                    capital: 1_000_000m, perTradeRiskRatio: 0.01m, stopLossDistancePerShare: 0.01m,
                    referencePrice: price, maxOrderAmount: 1_000_000m, availableCapital: available)
                .Should().BeGreaterThanOrEqualTo(1, "下界が偽なら残枠の金額キャップで 1 株は買える（省けば結論が変わる）");
        }
    }
}
