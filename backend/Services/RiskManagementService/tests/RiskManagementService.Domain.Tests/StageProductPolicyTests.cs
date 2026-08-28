using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Domain.Tests;

// FR-20, FR-19, ADR-0016 決定8・決定14, #333, IADR-0139: 段階別の商品種別強制を固定する。
//
// 3 点セット: マトリクス／境界値（equity $4,999 / $5,000）／
// **否定形**（Stage 2 で信用買い・空売りが通らない・申告値で迂回できない・**手仕舞いは止まらない**・
// 解禁条件の供給が無ければ空売りは開かない）。
public class StageProductPolicyTests
{
    // #364, IADR-0152 決定3: 基準通貨が USD になったため、equity と解禁下限は同一通貨であり近似換算が要らない。
    private static readonly decimal ReleaseEquity = StageProductPolicy.ShortSellLiveReleaseEquityUsd;

    private static readonly StageProductPolicy.StageReleaseContext Revalidated = new(true);

    // ------------------------------------------------------------------
    // 1. 段階 × 商品種別のマトリクス（ADR-0016 決定8 の表）
    // ------------------------------------------------------------------

    [Theory]
    // Stage 0（検証）・Stage 1（SIMULATE）: 3 種すべてを検証する（段階としての制限は課さない）。
    [InlineData(TradingStage.Stage0Verification, ProductType.Cash, null)]
    [InlineData(TradingStage.Stage0Verification, ProductType.MarginLong, null)]
    [InlineData(TradingStage.Stage0Verification, ProductType.ShortSell, null)]
    [InlineData(TradingStage.Stage1Simulate, ProductType.Cash, null)]
    [InlineData(TradingStage.Stage1Simulate, ProductType.MarginLong, null)]
    [InlineData(TradingStage.Stage1Simulate, ProductType.ShortSell, null)]
    // Stage 2（最小実弾）: **現物のみ**。
    [InlineData(TradingStage.Stage2MinimalLive, ProductType.Cash, null)]
    [InlineData(TradingStage.Stage2MinimalLive, ProductType.MarginLong, RejectionReason.StageProductTypeProhibited)]
    [InlineData(TradingStage.Stage2MinimalLive, ProductType.ShortSell, RejectionReason.StageProductTypeProhibited)]
    // Stage 3（段階増額）: 信用買いは無条件。空売りは条件付き（本ケースは条件充足）。
    [InlineData(TradingStage.Stage3ScaledLive, ProductType.Cash, null)]
    [InlineData(TradingStage.Stage3ScaledLive, ProductType.MarginLong, null)]
    [InlineData(TradingStage.Stage3ScaledLive, ProductType.ShortSell, null)]
    public void 段階別の商品種別マトリクスは計画どおりである(
        TradingStage stage, ProductType productType, RejectionReason? expected)
    {
        StageProductPolicy.Evaluate(stage, productType, ReleaseEquity, Revalidated).Should().Be(expected);
    }

    // **否定形**（ADR-0016 決定8）: Stage 2 は初めて実資金を投じる段階であり、損失上限の無い取引を
    // 持ち込まない。信用買いも空売りも通らない。
    [Theory]
    [InlineData(ProductType.MarginLong)]
    [InlineData(ProductType.ShortSell)]
    public void Stage2では解禁条件を満たしていても信用買いと空売りは通らない(ProductType productType)
    {
        // equity も Stage 0 再充足も満たした状態でも、段階が Stage 2 である限り通らない。
        StageProductPolicy.Evaluate(TradingStage.Stage2MinimalLive, productType, ReleaseEquity * 10m, Revalidated)
            .Should().Be(RejectionReason.StageProductTypeProhibited);
    }

    // ------------------------------------------------------------------
    // 2. Stage 3 の空売り実弾解禁条件（決定8・決定14 の AND）
    // ------------------------------------------------------------------

    // **境界値**: 「1 銘柄あたり上限 $500 以上（＝**自己資金 $5,000 以上**）」。ちょうど $5,000 は解禁される。
    [Fact]
    public void 空売りの実弾解禁はequity5000ドルちょうどから許される()
    {
        StageProductPolicy.Evaluate(
                TradingStage.Stage3ScaledLive, ProductType.ShortSell, ReleaseEquity, Revalidated)
            .Should().BeNull("$5,000 ちょうどは解禁条件を満たす");

        StageProductPolicy.Evaluate(
                TradingStage.Stage3ScaledLive, ProductType.ShortSell, ReleaseEquity - 1m, Revalidated)
            .Should().Be(RejectionReason.StageShortSellReleaseUnmet, "$5,000 未満は解禁されない");
    }

    // 計画の等値変換（「1 銘柄あたり上限 $500 以上」＝「自己資金 $5,000 以上」）が成り立つことを固定する。
    // 1 銘柄あたり上限は equity の 10%（ADR-0016 決定2(a)）である。
    [Fact]
    public void 解禁下限は1銘柄あたり空売り上限500ドルと等価である()
    {
        StageProductPolicy.ShortSellLiveReleaseEquityUsd.Should().Be(5_000m);

        var limits = TradingDefaults.CreateShortSellSettings().Limits;
        limits.PerSymbolCapFor(StageProductPolicy.ShortSellLiveReleaseEquityUsd).Should().Be(500m);
    }

    // **否定形**（決定14）: 「空売りを含む戦略で Stage 0 の 7 条件を再度満たす」が欠ければ、
    // equity が足りていても解禁されない（2 条件の AND）。
    [Fact]
    public void 空売りを含む戦略のStage0再充足が無ければequityが足りても解禁されない()
    {
        StageProductPolicy.Evaluate(
                TradingStage.Stage3ScaledLive,
                ProductType.ShortSell,
                ReleaseEquity * 100m,
                new StageProductPolicy.StageReleaseContext(ShortSellStrategyBacktestPassed: false))
            .Should().Be(RejectionReason.StageShortSellReleaseUnmet);
    }

    // **否定形**（フェイルクローズ）: 解禁条件を確認する経路が無い（供給元が未実装＝`null`）なら空売りは開かない。
    // 「確認できないまま通す」を許すと、統制が実質的に存在しないのと同じになる（IADR-0131 決定4 と同じ規律）。
    [Fact]
    public void 解禁条件の供給が無ければ空売りは開かない()
    {
        StageProductPolicy.Evaluate(
                TradingStage.Stage3ScaledLive, ProductType.ShortSell, ReleaseEquity * 100m, release: null)
            .Should().Be(RejectionReason.StageShortSellReleaseUnmet);
    }

    // **否定形**（安全側の既定）: 未知の段階（enum の追加）は新規建てを通さない。
    [Fact]
    public void 未知の段階は新規建てを通さない()
    {
        StageProductPolicy.Evaluate((TradingStage)99, ProductType.Cash, ReleaseEquity, Revalidated)
            .Should().Be(RejectionReason.StageProductTypeProhibited);
    }

    // FR-20, #333: 段階制約による拒否は**クラス B** であり、「統制違反 0 件」（クラス C 限定）の件数に
    // 影響しない。混ぜると段階昇格ゲートが恒久ブロックになる（06_daytrading-review §4.1）。
    [Theory]
    [InlineData(RejectionReason.StageProductTypeProhibited)]
    [InlineData(RejectionReason.StageShortSellReleaseUnmet)]
    public void 段階制約による拒否は統制違反に計上しない(RejectionReason reason)
    {
        RejectionReasonClassification.ClassOf(reason).Should().Be(RejectionReasonClass.B);
        RejectionReasonClassification.CountsAsControlViolation(reason).Should().BeFalse();
    }
}
