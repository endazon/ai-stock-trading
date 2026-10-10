using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, UC-06, ADR-0063 決定1〜5, #1291, IADR-0527: 高ボラティリティ銘柄の区分（ATR(14) ÷ 参照価格 ≥ 4% の自動判定と利用者の明示指定の併用）と、
// 区分の 1 注文あたりの発注金額上限（equity の 5%。区分外の 25% との小さい方）。
//
//   1. 境界値 — ちょうど 4%・4% 直下・ATR の欠損・明示指定と自動判定の両方
//   2. 既存の上限との関係 — 区分外は従来の 25% のまま／区分の銘柄は 25% を超えない（二重に効かない・矛盾しない）
//   3. 審査（RiskEvaluator）— 明示指定・発注意図が運ぶ ATR のどちらでも 5% で拒否し、決済は止めない
//   4. 値域 — 最小の名目額の比率 以上 〜 25% 以下
public class HighVolatilityOrderCapTests
{
    private const decimal Equity = 100_000m;

    private static RiskLimitSettings Limits => TradingDefaults.CreateRiskLimits();

    private static HighVolatilitySettings Designated(params string[] symbols) =>
        TradingDefaults.CreateHighVolatilitySettings() with
        {
            DesignatedSymbols = [.. symbols.Select(s => new HighVolatilitySymbol(s, Market.UnitedStates))],
        };

    private static PortfolioSnapshot Snapshot() => new() { Capital = Equity };

    // 価格 100 の銘柄を notional ドル分（端数は価格へ寄せる）。atr はローカル通貨の ATR(14)。
    private static OrderIntent Entry(string symbol, decimal notional, decimal? atr = null, TradeSide side = TradeSide.Buy) =>
        new(symbol, Market.UnitedStates, side, ProductType.Cash, BrokerProvider.InternalPaper,
            1, notional, PositionEffect.Open, StopLossPrice: null, Atr14: atr);

    // ------------------------------------------------------------------
    // 1. 区分の判定の境界値
    // ------------------------------------------------------------------

    // T-10-2540, ADR-0063 決定1: 自動判定は ATR ÷ 参照価格 ≥ 4%。ちょうど 4% は区分に入り、直下は入らない。
    [Theory]
    [InlineData(4.00, 100.0, true)]    // ちょうど 4%
    [InlineData(3.99, 100.0, false)]   // 直下
    [InlineData(4.01, 100.0, true)]    // 直上
    [InlineData(15.388, 384.70, true)] // TSLA 規模でもちょうど 4%（比は通貨・価格の桁に依らない）
    [InlineData(15.387, 384.70, false)]
    public void T_10_2540_自動判定はATR比4パーセントちょうどで区分に入る(double atr, double price, bool expected)
    {
        HighVolatilityOrderCap.IsAutoClassified((decimal)atr, (decimal)price).Should().Be(expected);
    }

    // T-10-2541, ADR-0063 決定1 🔴: ATR が得られない（null・0 以下）か参照価格が正でなければ自動判定は働かない（明示指定だけで判定する）。
    [Theory]
    [InlineData(null, 100.0)]
    [InlineData(0.0, 100.0)]
    [InlineData(-5.0, 100.0)]
    [InlineData(10.0, 0.0)]
    public void T_10_2541_ATRが欠損していれば自動判定は働かない(double? atr, double price)
    {
        HighVolatilityOrderCap.IsAutoClassified((decimal?)atr, (decimal)price).Should().BeFalse();
        HighVolatilityOrderCap.IsHighVolatility(Designated(), "TSLA", Market.UnitedStates, (decimal?)atr, (decimal)price)
            .Should().BeFalse("明示指定が無く ATR も無ければ区分外（緩い側へ倒れる。決定1）");
        HighVolatilityOrderCap.IsHighVolatility(Designated("TSLA"), "TSLA", Market.UnitedStates, (decimal?)atr, (decimal)price)
            .Should().BeTrue("ATR が無くても明示指定があれば区分に入る");
    }

    // T-10-2542, ADR-0063 決定1: 判定は「明示指定 または 自動判定」。両方に当たっても区分は 1 つで、上限は 1 回だけ（5%）掛かる。
    [Theory]
    [InlineData(true, 5.0, true)]   // 両方
    [InlineData(true, 1.0, true)]   // 明示指定だけ（ATR 1%）
    [InlineData(false, 5.0, true)]  // 自動判定だけ
    [InlineData(false, 1.0, false)] // どちらでもない
    public void T_10_2542_明示指定と自動判定のどちらかで区分に入る(bool designated, double atr, bool expected)
    {
        var settings = designated ? Designated("TSLA") : Designated();

        var isHv = HighVolatilityOrderCap.IsHighVolatility(settings, "TSLA", Market.UnitedStates, (decimal)atr, 100m);

        isHv.Should().Be(expected);
        HighVolatilityOrderCap.MaxOrderAmountFor(Limits, settings, Equity, isHv)
            .Should().Be(expected ? 5_000m : 25_000m, "両方に当たっても 5% が 1 回だけ（5% × 5% のように重ならない）");
    }

    // T-10-2543, ADR-0063 決定1: 明示指定は銘柄コードの大小文字・前後の空白を無視し、市場は区別する（取りこぼして緩い側へ倒さない）。
    [Fact]
    public void T_10_2543_明示指定は銘柄コードの大小文字と空白を無視し市場は区別する()
    {
        var settings = Designated(" tsla ");

        settings.IsDesignated("TSLA", Market.UnitedStates).Should().BeTrue();
        settings.IsDesignated("tsla", Market.UnitedStates).Should().BeTrue();
        settings.IsDesignated("TSLA", Market.Japan).Should().BeFalse("市場が違えば別の銘柄");
        settings.IsDesignated("NVDA", Market.UnitedStates).Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // 2. 既存の上限との関係（二重に効かない・矛盾しない）
    // ------------------------------------------------------------------

    // T-10-2544, ADR-0063 決定3・決定5: 区分外は従来の MaxOrderAmountFor（25%）そのもの。区分の銘柄は 5%。
    // 区分外の上限を 5% より下げた構成では、区分の銘柄も区分外と同じ値（区分が区分外より緩くならない）。
    [Theory]
    [InlineData(0.25, false, 25_000)]
    [InlineData(0.25, true, 5_000)]
    [InlineData(0.03, false, 3_000)]
    [InlineData(0.03, true, 3_000)] // 区分の 5% より区分外の 3% が厳しい → 3%
    public void T_10_2544_区分の上限は区分外の上限との小さい方(double standardRatio, bool isHv, int expected)
    {
        var limits = Limits with { MaxOrderAmountRatio = (decimal)standardRatio };

        HighVolatilityOrderCap.MaxOrderAmountFor(limits, TradingDefaults.CreateHighVolatilitySettings(), Equity, isHv)
            .Should().Be(expected);
        if (!isHv)
        {
            HighVolatilityOrderCap.MaxOrderAmountFor(limits, TradingDefaults.CreateHighVolatilitySettings(), Equity, false)
                .Should().Be(limits.MaxOrderAmountFor(Equity), "区分外は既存の解決と同一（挙動を変えない）");
        }
    }

    // ------------------------------------------------------------------
    // 3. 審査（RiskEvaluator）
    // ------------------------------------------------------------------

    // T-10-2545, ADR-0063 決定1・決定2: 明示指定の銘柄は equity の 5% を超えれば PerOrderAmountExceeded。ちょうど 5% は通す（超過のみ拒否）。
    [Theory]
    [InlineData(4_999, false)]
    [InlineData(5_000, false)] // 一致
    [InlineData(5_001, true)]  // 直上
    public void T_10_2545_明示指定の銘柄は5パーセントの境界で拒否に切り替わる(int notional, bool rejected)
    {
        var settings = TradingDefaults.CreateSettings() with { HighVolatility = Designated("TSLA") };

        var result = RiskEvaluator.Evaluate(Entry("TSLA", notional), settings, Snapshot());

        result.Reasons.Contains(RejectionReason.PerOrderAmountExceeded).Should().Be(rejected);
    }

    // T-10-2546, ADR-0063 決定1・決定3: 発注意図が運ぶ ATR(14) で自動判定する（ATR ÷ 価格 = ちょうど 4% は 5% の上限）。
    // ATR が無い（null）・4% 未満なら区分外の 25% のまま（同じ 6% の注文が通る）。
    [Theory]
    [InlineData(240.0, true)]   // 6,000 ドルの 1 株に ATR 240＝ちょうど 4% → 区分 → 6% は拒否
    [InlineData(239.0, false)]  // 3.98% → 区分外 → 6% は 25% 以内で通る
    [InlineData(null, false)]   // ATR 欠損 → 明示指定だけ（無い）→ 区分外
    public void T_10_2546_発注意図のATRで区分を判定する(double? atr, bool rejected)
    {
        var result = RiskEvaluator.Evaluate(Entry("NVDA", 6_000m, (decimal?)atr), TradingDefaults.CreateSettings(), Snapshot());

        result.Reasons.Contains(RejectionReason.PerOrderAmountExceeded).Should().Be(rejected);
    }

    // T-10-2547, ADR-0063 決定3: 区分外の銘柄は従来どおり 25% で切り替わる（区分の統制が区分外を締めない）。
    [Theory]
    [InlineData(25_000, false)]
    [InlineData(25_001, true)]
    public void T_10_2547_区分外の銘柄は従来の25パーセントのまま(int notional, bool rejected)
    {
        var settings = TradingDefaults.CreateSettings() with { HighVolatility = Designated("TSLA") };

        RiskEvaluator.Evaluate(Entry("AAPL", notional, atr: 1m), settings, Snapshot())
            .Reasons.Contains(RejectionReason.PerOrderAmountExceeded).Should().Be(rejected);
    }

    // T-10-2548, ADR-0063 決定4: 決済（手仕舞い）は区分の上限で止めない（遡及しない・閉じられないより止められないほうが安全）。
    [Fact]
    public void T_10_2548_区分の銘柄の決済は止めない()
    {
        var settings = TradingDefaults.CreateSettings() with { HighVolatility = Designated("TSLA") };
        var exit = new OrderIntent("TSLA", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper,
            1, 236_353m, PositionEffect.Close, Atr14: 50m);

        RiskEvaluator.Evaluate(exit, settings, Snapshot()).Reasons.Should().NotContain(RejectionReason.PerOrderAmountExceeded);
    }

    // T-10-2549, ADR-0063 §実測 8: 観測の TSLA（equity 971,017・614 株 @384.94＝$236,353）は、明示指定があれば審査で止まる。
    // 区分の上限は約 $48,551（126 株＝$48,502 は通る）。
    [Fact]
    public void T_10_2549_観測のTSLAの1件は区分の上限で止まる()
    {
        var settings = TradingDefaults.CreateSettings() with { HighVolatility = Designated("TSLA") };
        var snapshot = new PortfolioSnapshot { Capital = 971_017m };
        OrderIntent Tsla(int quantity) => new("TSLA", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            BrokerProvider.InternalPaper, quantity, 384.94m, PositionEffect.Open);

        RiskEvaluator.Evaluate(Tsla(614), settings, snapshot).Reasons.Should().Contain(RejectionReason.PerOrderAmountExceeded);
        RiskEvaluator.Evaluate(Tsla(126), settings, snapshot).Reasons.Should().NotContain(RejectionReason.PerOrderAmountExceeded);
        RiskEvaluator.Evaluate(Tsla(127), settings, snapshot).Reasons.Should().Contain(RejectionReason.PerOrderAmountExceeded);
    }

    // ------------------------------------------------------------------
    // 4. 値域（ADR-0063 決定2: 最小の名目額の比率 以上 〜 25% 以下）
    // ------------------------------------------------------------------

    // T-10-2550, ADR-0063 決定2: 上限の構成範囲は 0.01（最小の名目額の比率）以上 0.25 以下。両端は受理、外は拒否。
    [Theory]
    [InlineData(0.01, true)]
    [InlineData(0.05, true)]
    [InlineData(0.25, true)]
    [InlineData(0.0099, false)]
    [InlineData(0.2501, false)]
    [InlineData(0.0, false)]
    public void T_10_2550_区分の上限の値域は最小の名目額の比率から25パーセント(double ratio, bool valid)
    {
        var settings = TradingDefaults.CreateHighVolatilitySettings() with { MaxOrderAmountRatio = (decimal)ratio };

        HighVolatilityOrderCap.Validate(settings).Should().HaveCount(valid ? 0 : 1);
        HighVolatilityOrderCap.MinRatioLowerBound.Should().Be(TradingDefaults.MinEntryNotionalRatio);
        HighVolatilityOrderCap.MaxRatioUpperBound.Should().Be(TradingDefaults.CreateRiskLimits().MaxOrderAmountRatio);
    }

    // T-10-2551, ADR-0063 決定1: 明示指定の銘柄コードは空にできず、市場は既知の値に限る。
    [Fact]
    public void T_10_2551_明示指定の銘柄コードの空と未知の市場は拒否する()
    {
        var settings = TradingDefaults.CreateHighVolatilitySettings() with
        {
            DesignatedSymbols = [new HighVolatilitySymbol(" ", Market.UnitedStates), new HighVolatilitySymbol("TSLA", (Market)99)],
        };

        HighVolatilityOrderCap.Validate(settings).Should().HaveCount(2);
        var act = () => HighVolatilityOrderCap.ThrowIfInvalid(settings);
        act.Should().Throw<ArgumentException>();
    }
}
