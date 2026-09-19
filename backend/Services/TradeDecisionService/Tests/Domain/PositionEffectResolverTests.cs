using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Domain;
using AwesomeAssertions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, FR-05, FR-10, #292, IADR-0119: 判断由来の建玉効果の決定（純関数）。
//
// #865, IADR-0358: requireKnownHoldingForOpen は**必須引数**である（IADR-0163 決定2 第 1 節。
// 省略できると渡し忘れが「統制なし」になる）。false＝未結線（NoOp＝照会していない）／true＝実結線。
public class PositionEffectResolverTests
{
    [Fact]
    public void ロング保有への売りは全量の決済になる()
    {
        var decision = PositionEffectResolver.Resolve(
            TradeSide.Sell, signedHeldQuantity: 4072, requireKnownHoldingForOpen: false);

        decision.Effect.Should().Be(PositionEffect.Close);
        decision.CloseQuantity.Should().Be(4072, "出口の数量は保有数であってサイジング結果ではない");
        decision.IsClose.Should().BeTrue();
    }

    [Fact]
    public void ショート保有への買いは全量の決済になる()
    {
        var decision = PositionEffectResolver.Resolve(
            TradeSide.Buy, signedHeldQuantity: -100, requireKnownHoldingForOpen: false);

        decision.Effect.Should().Be(PositionEffect.Close);
        decision.CloseQuantity.Should().Be(100, "符号を落として正の数量にする");
    }

    [Fact]
    public void ロング保有への買いは建て増しの新規建てになる()
    {
        var decision = PositionEffectResolver.Resolve(
            TradeSide.Buy, signedHeldQuantity: 100, requireKnownHoldingForOpen: false);

        decision.Effect.Should().Be(PositionEffect.Open);
        decision.CloseQuantity.Should().Be(0);
    }

    [Fact]
    public void ショート保有への売りは建て増しの新規建てになる()
    {
        PositionEffectResolver.Resolve(TradeSide.Sell, signedHeldQuantity: -100, requireKnownHoldingForOpen: false)
            .Effect.Should().Be(PositionEffect.Open);
    }

    [Fact]
    public void 保有なしの買いは新規建てになる()
    {
        PositionEffectResolver.Resolve(TradeSide.Buy, signedHeldQuantity: 0, requireKnownHoldingForOpen: false)
            .Effect.Should().Be(PositionEffect.Open);
    }

    [Fact]
    public void 保有なしの売りは見送る()
    {
        // 裸の新規ショート建て。現物のみ有効な段階では成立せず、ガード（商品種別/市場）は方向を見ないため素通りする。
        var decision = PositionEffectResolver.Resolve(
            TradeSide.Sell, signedHeldQuantity: 0, requireKnownHoldingForOpen: false);

        decision.IsSkipped.Should().BeTrue();
        decision.Effect.Should().BeNull();
    }

    [Fact]
    public void 建玉が不明なら売りを見送る()
    {
        // null（照会不能＝不明）は 0（保有なし）と意味が異なるが、売りについてはどちらも安全側＝見送り。
        // ADR-0003「方針の範囲外・不確実な場合は必ず Hold」。
        PositionEffectResolver.Resolve(TradeSide.Sell, signedHeldQuantity: null, requireKnownHoldingForOpen: false)
            .IsSkipped.Should().BeTrue();
    }

    [Fact]
    public void 建玉が不明でも照会先が未結線なら買いは従来どおり新規建てになる()
    {
        // 未結線（NoOp＝常に不明）は「照会していない」であり、既定構成の新規建てを一律に止めない（IADR-0119 決定2）。
        PositionEffectResolver.Resolve(TradeSide.Buy, signedHeldQuantity: null, requireKnownHoldingForOpen: false)
            .Effect.Should().Be(PositionEffect.Open);
    }

    // --- FR-04, FR-10, ADR-0003, #865, IADR-0358: 実結線のもとで保有が不明なら新規建てを見送る ---

    [Fact]
    public void 実結線で建玉が不明なら買いの新規建てを見送る()
    {
        var decision = PositionEffectResolver.Resolve(
            TradeSide.Buy, signedHeldQuantity: null, requireKnownHoldingForOpen: true);

        decision.IsSkipped.Should().BeTrue();
        decision.Effect.Should().BeNull();
    }

    [Fact]
    public void 実結線で建玉が不明なら売りの建て増しも見送る()
    {
        // 不明の売りは元から見送り（裸の新規ショート建て・IADR-0119 決定2）。判定が重なっても結果は変わらない。
        PositionEffectResolver.Resolve(TradeSide.Sell, signedHeldQuantity: null, requireKnownHoldingForOpen: true)
            .IsSkipped.Should().BeTrue();
    }

    // 🔴 出口は塞がない（肯定形）。保有が判っていれば、実結線の判定を有効にしても決済はそのまま通る。
    [Theory]
    [InlineData(4072, (int)TradeSide.Sell, 4072)]
    [InlineData(-100, (int)TradeSide.Buy, 100)]
    public void 実結線の判定を有効にしても保有が判っていれば決済は通る(int held, int side, int expectedQuantity)
    {
        var decision = PositionEffectResolver.Resolve(
            (TradeSide)side, signedHeldQuantity: held, requireKnownHoldingForOpen: true);

        decision.Effect.Should().Be(PositionEffect.Close);
        decision.CloseQuantity.Should().Be(expectedQuantity);
    }

    // 保有が判っているとき（あり／なし）は挙動が変わらない。
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void 実結線でも保有が判っていれば買いは従来どおり新規建てになる(int held)
    {
        PositionEffectResolver.Resolve(TradeSide.Buy, signedHeldQuantity: held, requireKnownHoldingForOpen: true)
            .Effect.Should().Be(PositionEffect.Open);
    }
}
