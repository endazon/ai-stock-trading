using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Domain;
using AwesomeAssertions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, FR-05, FR-10, #292, IADR-0119: 判断由来の建玉効果の決定（純関数）。
public class PositionEffectResolverTests
{
    [Fact]
    public void ロング保有への売りは全量の決済になる()
    {
        var decision = PositionEffectResolver.Resolve(TradeSide.Sell, signedHeldQuantity: 4072);

        decision.Effect.Should().Be(PositionEffect.Close);
        decision.CloseQuantity.Should().Be(4072, "出口の数量は保有数であってサイジング結果ではない");
        decision.IsClose.Should().BeTrue();
    }

    [Fact]
    public void ショート保有への買いは全量の決済になる()
    {
        var decision = PositionEffectResolver.Resolve(TradeSide.Buy, signedHeldQuantity: -100);

        decision.Effect.Should().Be(PositionEffect.Close);
        decision.CloseQuantity.Should().Be(100, "符号を落として正の数量にする");
    }

    [Fact]
    public void ロング保有への買いは建て増しの新規建てになる()
    {
        var decision = PositionEffectResolver.Resolve(TradeSide.Buy, signedHeldQuantity: 100);

        decision.Effect.Should().Be(PositionEffect.Open);
        decision.CloseQuantity.Should().Be(0);
    }

    [Fact]
    public void ショート保有への売りは建て増しの新規建てになる()
    {
        PositionEffectResolver.Resolve(TradeSide.Sell, signedHeldQuantity: -100)
            .Effect.Should().Be(PositionEffect.Open);
    }

    [Fact]
    public void 保有なしの買いは新規建てになる()
    {
        PositionEffectResolver.Resolve(TradeSide.Buy, signedHeldQuantity: 0)
            .Effect.Should().Be(PositionEffect.Open);
    }

    [Fact]
    public void 保有なしの売りは見送る()
    {
        // 裸の新規ショート建て。現物のみ有効な段階では成立せず、ガード（商品種別/市場）は方向を見ないため素通りする。
        var decision = PositionEffectResolver.Resolve(TradeSide.Sell, signedHeldQuantity: 0);

        decision.IsSkipped.Should().BeTrue();
        decision.Effect.Should().BeNull();
    }

    [Fact]
    public void 建玉が不明なら売りを見送る()
    {
        // null（照会不能＝不明）は 0（保有なし）と意味が異なるが、売りについてはどちらも安全側＝見送り。
        // ADR-0003「方針の範囲外・不確実な場合は必ず Hold」。
        PositionEffectResolver.Resolve(TradeSide.Sell, signedHeldQuantity: null)
            .IsSkipped.Should().BeTrue();
    }

    [Fact]
    public void 建玉が不明でも買いは従来どおり新規建てになる()
    {
        // 買いは裸になり得ず、金額系上限がそのまま効く（不明を理由に取引機会を落とさない）。
        PositionEffectResolver.Resolve(TradeSide.Buy, signedHeldQuantity: null)
            .Effect.Should().Be(PositionEffect.Open);
    }
}
