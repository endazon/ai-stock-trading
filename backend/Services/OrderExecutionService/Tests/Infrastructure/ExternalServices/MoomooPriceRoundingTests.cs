using OrderExecutionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, FR-12, ADR-0040 決定1（S3）, #844, IADR-0347: ブローカーへ送る価格の丸め。
// 稼働環境で `retType=-1 The precision of Price in Place Order does not meet the specification.` を実測し、
// 保護レグが拒否された（発火価格 × 0.99 が小数 4 桁になっていた）。境界と向きをここで固定する。
public class MoomooPriceRoundingTests
{
    // T-10-390: 米国株は小数 2 桁（1 ドル未満はサブペニーの 4 桁）、日本株は円単位。
    [Theory]
    [InlineData(Market.UnitedStates, 329.0265, 2)]
    [InlineData(Market.UnitedStates, 0.98765, 4)]
    [InlineData(Market.Japan, 1234.56, 0)]
    public void 市場ごとの小数桁を返す(Market market, decimal price, int expected) =>
        MoomooPriceRounding.DecimalsFor(market, price).Should().Be(expected);

    // T-10-391: 指値は**約定しやすい側**へ丸める（売りは切り下げ・買いは切り上げ）。保護が緩む側へ倒さない。
    [Theory]
    [InlineData(TradeSide.Sell, 329.0265, 329.02)]
    [InlineData(TradeSide.Buy, 329.0265, 329.03)]
    [InlineData(TradeSide.Sell, 329.02, 329.02)]
    [InlineData(TradeSide.Buy, 329.02, 329.02)]
    public void 指値は約定しやすい側へ丸める(TradeSide side, decimal raw, decimal expected) =>
        MoomooPriceRounding.RoundLimit(Market.UnitedStates, side, raw, referencePrice: 332.35m)
            .Should().Be(expected);

    // T-10-420: #846。**エントリーの指値は不利にならない側**へ丸める（買いは切り下げ・売りは切り上げ）。
    // 🔴 保護レグ用の `RoundLimit`（約定しやすい側）とは**向きが逆**である。
    // エントリーは約定しなくても損をしない（見送りは可逆）が、意図より悪い価格で建つと
    // 「エントリー − 損切りライン」の実幅が広がり、1 株あたりリスクの前提を超える（不可逆）。
    [Theory]
    [InlineData(TradeSide.Buy, 329.0265, 329.02)]
    [InlineData(TradeSide.Sell, 329.0265, 329.03)]
    [InlineData(TradeSide.Buy, 329.02, 329.02)]   // 既に刻みに乗っていれば動かさない
    [InlineData(TradeSide.Sell, 329.02, 329.02)]
    public void エントリーの指値は不利にならない側へ丸める(TradeSide side, decimal raw, decimal expected)
    {
        MoomooPriceRounding.RoundEntryLimit(Market.UnitedStates, side, raw).Should().Be(expected);

        // 否定形: 保護レグ用の向き（約定しやすい側）にはなっていない。取り違えるとエントリーが不利側で建つ。
        if (raw != expected)
        {
            MoomooPriceRounding.RoundEntryLimit(Market.UnitedStates, side, raw)
                .Should().NotBe(MoomooPriceRounding.RoundLimit(Market.UnitedStates, side, raw, raw));
        }
    }

    // T-10-421: エントリーの指値の桁も市場と価格帯で決まる（米国 2 桁・1 ドル未満 4 桁・日本 0 桁）。
    // 境界（1 ドルちょうど）と極端な価格も固定する。**実弾に当たる経路**である。
    [Theory]
    [InlineData(Market.UnitedStates, TradeSide.Buy, 0.98765, 0.9876)]
    [InlineData(Market.UnitedStates, TradeSide.Sell, 0.98765, 0.9877)]
    [InlineData(Market.UnitedStates, TradeSide.Buy, 1.0, 1.0)]              // 1 ドルちょうどは 2 桁側（境界）
    [InlineData(Market.UnitedStates, TradeSide.Sell, 0.9999999, 1.0)]       // 4 桁の切り上げが 1 ドルを跨ぐ
    [InlineData(Market.Japan, TradeSide.Buy, 1234.56, 1234)]
    [InlineData(Market.Japan, TradeSide.Sell, 1234.56, 1235)]
    [InlineData(Market.Japan, TradeSide.Buy, 0.4, 0)]                       // 刻み未満は 0（発注前検証が止める）
    [InlineData(Market.UnitedStates, TradeSide.Sell, 999999.999, 1000000.0)]
    public void エントリーの指値の桁は市場と価格帯で決まる(
        Market market, TradeSide side, decimal raw, decimal expected) =>
        MoomooPriceRounding.RoundEntryLimit(market, side, raw).Should().Be(expected);

    // T-10-392: 発火価格は**早く発火する側**へ丸める（ロングの保護＝売りは切り上げ、ショートの保護は切り下げ）。
    [Theory]
    [InlineData(TradeSide.Sell, 332.3512, 332.36)]
    [InlineData(TradeSide.Buy, 332.3512, 332.35)]
    public void 発火価格は早く発火する側へ丸める(TradeSide closeSide, decimal raw, decimal expected) =>
        MoomooPriceRounding.RoundTrigger(Market.UnitedStates, closeSide, raw).Should().Be(expected);

    // T-10-393: 丸めで指値が発火価格へ寄り切ったら 1 刻みだけ離す。
    // ずらし幅が刻みより小さいと同値になり、「保護レグを置いたのに約定しない」を作る。
    [Fact]
    public void 丸めで発火価格と同値になったら一刻みだけ離す()
    {
        MoomooPriceRounding.EnsureBeyondTrigger(Market.UnitedStates, TradeSide.Sell, 332.35m, 332.35m)
            .Should().Be(332.34m);
        MoomooPriceRounding.EnsureBeyondTrigger(Market.UnitedStates, TradeSide.Buy, 332.35m, 332.35m)
            .Should().Be(332.36m);
        MoomooPriceRounding.EnsureBeyondTrigger(Market.Japan, TradeSide.Sell, 1234m, 1234m)
            .Should().Be(1233m);
        // 既に離れていれば動かさない。
        MoomooPriceRounding.EnsureBeyondTrigger(Market.UnitedStates, TradeSide.Sell, 329.02m, 332.35m)
            .Should().Be(329.02m);
    }

    // T-10-397: #845 の監査。桁は**銘柄の基準価格（発火価格）で一度だけ**決める。
    // 値ごとに 1 ドルと比べると、1 ドル近傍で指値 4 桁・発火価格 2 桁のように桁が混ざり、
    // ブローカーの判定が銘柄価格で決まるなら同じ拒否が再発する。
    [Fact]
    public void 桁は基準価格で一度だけ決める()
    {
        // 基準 1.004 ドル（＝2 桁の銘柄）。指値が 1 ドルを割っても 2 桁のまま。
        MoomooPriceRounding.RoundLimit(Market.UnitedStates, TradeSide.Sell, 0.99396m, referencePrice: 1.004m)
            .Should().Be(0.99m);
        // 基準が 1 ドル未満ならサブペニー（4 桁）。
        MoomooPriceRounding.RoundLimit(Market.UnitedStates, TradeSide.Sell, 0.987654m, referencePrice: 0.98m)
            .Should().Be(0.9876m);
    }

    // T-10-394: トレール幅は**狭い側**（早く発火する側）へ丸める。
    // 刻みに満たない幅は 0 のままにする——1 刻みを足すと「幅 0 は送らず理由を残す」検証を無効化し、
    // 決定が求めていない保護距離を捏造することになる（既存の発注前検証がその責務を持つ）。
    [Fact]
    public void トレール幅は狭い側へ丸め刻み未満は零のままにする()
    {
        MoomooPriceRounding.RoundTrail(Market.UnitedStates, 332.35m, 3.4567m).Should().Be(3.45m);
        MoomooPriceRounding.RoundTrail(Market.UnitedStates, 332.35m, 0.004m).Should().Be(0m);
        MoomooPriceRounding.RoundTrail(Market.Japan, 1234m, 0.4m).Should().Be(0m);
    }
}
