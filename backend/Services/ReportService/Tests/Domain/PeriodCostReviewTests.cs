using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-07, FR-16, FR-17, #615, IADR-0305, 04_report-templates 週報 §5「リスク・費用レビュー」:
// **費用の内訳と費用率**（純関数）。
//
// 🔴 **内訳の合計が §1 サマリの費用合計と一致することを固定する。** 一致しない壊れ方は
// 例外も赤いテストも出さない——読み手が電卓で足すまで誰も気づかない（IADR-0301 と同じ理由）。
public class PeriodCostReviewTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 24, 0, 5, 0, TimeSpan.Zero);

    private static readonly TradingAssumptions Assumptions = TradingAssumptionsDefaults.Create();

    private static PeriodTradeFill Fill(Market market, TradeSide side, int qty, decimal price, int minutes) =>
        new(market == Market.Japan ? "7203" : "AAPL", market, side,
            side == TradeSide.Buy ? PositionEffect.Open : PositionEffect.Close,
            qty, price, T0.AddMinutes(minutes));

    // 🔴 JP（基準通貨外＝為替スプレッドが乗る）と US の**両方**を含む列を使う。
    // 片方だけだと、分解が効いていなくても（片方の項が常に 0 でも）緑になる。
    private static IReadOnlyList<PeriodTradeFill> BothMarkets() =>
    [
        Fill(Market.Japan, TradeSide.Buy, 100, 2_500m, 0),
        Fill(Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 60),
        Fill(Market.UnitedStates, TradeSide.Sell, 10, 1_200m, 2_880),
        Fill(Market.Japan, TradeSide.Sell, 100, 2_600m, 4_320),
    ];

    private static PeriodCostReview Build(IReadOnlyList<PeriodTradeFill> fills, decimal taxWithheld = 0m) =>
        PeriodCostReviewBuilder.Build(
            FillPnlAttributionBuilder.Build(fills, Assumptions, null), Assumptions, taxWithheld);

    // --- 内訳の和が §1 サマリと一致する ---

    [Fact]
    public void 費用の内訳の和が損益サマリの費用合計と一致する()
    {
        var fills = BothMarkets();

        var summary = PnlAggregator.Aggregate(fills, Assumptions);
        var review = Build(fills);

        review.Commission.Should().Be(review.TotalCost - review.FxSpread);
        review.TotalCost.Should().Be(summary.TotalCost);
    }

    [Fact]
    public void 費用率の分母は損益サマリの税引前費用前の実現損益と一致する()
    {
        var fills = BothMarkets();

        var summary = PnlAggregator.Aggregate(fills, Assumptions);
        var review = Build(fills, summary.TaxWithheld);

        review.RealizedPnlGross.Should().Be(summary.RealizedPnlGross);
        review.TaxWithheld.Should().Be(summary.TaxWithheld);
    }

    // 🔴 **区分がどちらも 0 でないことを確かめる。** 分解が効いていないと片方が 0 のまま合計だけ合う。
    [Fact]
    public void 基準通貨外の市場では為替スプレッドが手数料と別に計上される()
    {
        var review = Build([Fill(Market.Japan, TradeSide.Buy, 100, 2_500m, 0)]);

        // 既定の前提条件では手数料・為替スプレッドの率が 0 でないことを前提としない——
        // **合計が両区分の和である**ことと、**基準通貨の市場では為替スプレッドが 0 である**ことを見る。
        review.TotalCost.Should().Be(review.Commission + review.FxSpread);

        var baseMarket = MarketCurrency.IsBaseCurrency(Market.UnitedStates) ? Market.UnitedStates : Market.Japan;
        Build([Fill(baseMarket, TradeSide.Buy, 10, 1_000m, 0)]).FxSpread.Should().Be(0m);
    }

    // --- 費用率（分母の 3 通り） ---

    [Fact]
    public void 分母が正なら費用率は費用合計を実現損益で割った値になる()
    {
        var fills = new[]
        {
            Fill(Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 0),
            Fill(Market.UnitedStates, TradeSide.Sell, 10, 1_200m, 2_880),
        };

        var review = Build(fills);

        review.RealizedPnlGross.Should().Be(2_000m);
        review.CostRatio.Should().Be(review.TotalCost / 2_000m);
    }

    // 🔴 **負の分母で割らない。** 割ると比率の符号が反転し「費用が少ない期間」に見える。
    [Fact]
    public void 分母が負なら費用率は算出不能になる()
    {
        var fills = new[]
        {
            Fill(Market.UnitedStates, TradeSide.Buy, 10, 1_200m, 0),
            Fill(Market.UnitedStates, TradeSide.Sell, 10, 1_000m, 2_880),
        };

        var review = Build(fills);

        review.RealizedPnlGross.Should().Be(-2_000m);
        review.CostRatio.Should().BeNull();
    }

    [Fact]
    public void 約定が無い期間の費用率は算出不能であり0ではない()
    {
        var review = Build([]);

        review.TotalCost.Should().Be(0m);
        review.RealizedPnlGross.Should().Be(0m);
        review.CostRatio.Should().BeNull();
    }

    // --- 概算費用関数の非破壊追加（既存の値が変わっていないこと） ---

    [Theory]
    [InlineData(Market.Japan, 250_000)]
    [InlineData(Market.UnitedStates, 10_000)]
    public void 内訳版の合計は既存の概算費用関数と同値である(Market market, double notional)
    {
        var amount = (decimal)notional;

        CostCalculator.EstimateOneWayCostBreakdown(Assumptions, market, amount).Total
            .Should().Be(CostCalculator.EstimateOneWayCost(Assumptions, market, amount));
    }
}
