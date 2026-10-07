using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-07, FR-16, FR-17, #615, IADR-0305, 計画 ADR-0035, #1201, IADR-0501, 04_report-templates 週報 §5「リスク・費用レビュー」:
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

    // 🔴 JP と US の**両方**、買いと売りの**両方**を含む列を使う（諸費用は US の売りだけに掛かる）。
    // 片方だけだと、分解が効いていなくても（片方の項が常に 0 でも）緑になる。
    private static IReadOnlyList<PeriodTradeFill> BothMarkets() =>
    [
        Fill(Market.Japan, TradeSide.Buy, 100, 2_500m, 0),
        Fill(Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 60),
        Fill(Market.UnitedStates, TradeSide.Sell, 10, 1_200m, 2_880),
        Fill(Market.Japan, TradeSide.Sell, 100, 2_600m, 4_320),
    ];

    private static PeriodCostReview Build(
        IReadOnlyList<PeriodTradeFill> fills, decimal taxWithheld = 0m, BorrowFeeRecord? borrowFees = null) =>
        PeriodCostReviewBuilder.Build(
            FillPnlAttributionBuilder.Build(fills, Assumptions, null), Assumptions, taxWithheld, borrowFees);

    private static BorrowFeeRecord BorrowFees(decimal amountUsd, int unavailable = 0) => new(
        [new BorrowFeeAccrued("TSLA", Market.UnitedStates, new DateOnly(2026, 8, 24), 0.06m, 10_000m, amountUsd, T0)],
        [.. Enumerable.Range(0, unavailable).Select(i =>
            new BorrowFeeAccrualUnavailable("TSLA", Market.UnitedStates, new DateOnly(2026, 8, 25).AddDays(i), "料率照会に失敗", T0))]);

    // --- 内訳の和が §1 サマリと一致する ---

    [Fact]
    public void 費用の内訳の和が損益サマリの費用合計と一致する()
    {
        var fills = BothMarkets();

        var summary = PnlAggregator.Aggregate(fills, Assumptions);
        var review = Build(fills);

        review.Total.TradingCost.Should().Be(review.Commission + review.RegulatoryFees);
        review.Total.TradingCost.Should().Be(summary.TotalCost);
    }

    // FR-06, 計画 ADR-0035 決定 1, #1201: 分母は**約定代金差額**（費用・税をいずれも控除しない値）。
    [Fact]
    public void 費用率の分母は損益サマリの約定代金差額と一致する()
    {
        var fills = BothMarkets();

        var summary = PnlAggregator.Aggregate(fills, Assumptions);
        var review = Build(fills, summary.TaxWithheld);

        review.TradeValueDifference.Should().Be(summary.RealizedPnlGross);
        review.TaxWithheld.Should().Be(summary.TaxWithheld);
    }

    // FR-06, FR-17, 計画 ADR-0035 決定 5, 05_trading-assumptions §2, #1201: 諸費用は**設定点**（前提条件の既定＝計画の暫定値）から読み、
    // **米国株の売り約定だけ**に掛かる。
    [Fact]
    public void 取引諸費用は米国株の売り約定にだけ設定点の料率で掛かる()
    {
        var review = Build(BothMarkets());

        // US 売り 10 株 × 1,200 = 12,000 → SEC 12,000 × 20.60 / 1e6 = 0.2472、TAF 10 × 0.000166 = 0.00166。
        review.RegulatoryFees.Should().Be(0.2472m + 0.00166m);
        review.Total.Amount.Should().Be(review.Commission + review.RegulatoryFees);
    }

    // 🔴 FR-06, 計画 ADR-0035 決定 4, #1201: **事後集計は約定ごとの為替スプレッドを持たない**（両替は入出金時のみ）。
    // 事前見積りの為替スプレッド率が 0 でない前提条件でも、事後集計の費用は手数料と諸費用だけであり、為替スプレッドは未供給。
    [Fact]
    public void 事後集計の為替スプレッドは事前見積りと別の値で実績が無ければ未供給()
    {
        var withSpread = Assumptions with { FxSpreadRatio = 0.01m };
        var fills = new[] { Fill(Market.Japan, TradeSide.Buy, 100, 2_500m, 0) };

        var review = PeriodCostReviewBuilder.Build(
            FillPnlAttributionBuilder.Build(fills, withSpread, null), withSpread, 0m, null);

        // 事前見積りには為替スプレッドが乗る（判断時の採算判定）。
        CostCalculator.EstimateOneWayCostBreakdown(withSpread, Market.Japan, TradeSide.Buy, 100, 250_000m).FxSpread.Should().Be(2_500m);
        // 事後集計には乗らない。実績（入出金時の両替）の供給元が無いため未供給。
        review.Total.TradingCost.Should().Be(0m);
        review.Total.FxSpread.Should().BeNull();
        review.Total.IsUnderstated.Should().BeTrue();
    }

    // FR-06, 計画 ADR-0035 決定 3, #1201: 借株料は費用合計に**含める**。照会できなければ 0 を積まず未供給（過小）。
    [Fact]
    public void 借株料は費用合計に含まれ_未供給なら0を積まず過小と判定される()
    {
        var fills = BothMarkets();
        var without = Build(fills);
        var with = Build(fills, borrowFees: BorrowFees(1.64m));

        without.Total.BorrowFee.Should().BeNull();
        without.Total.Amount.Should().Be(without.Total.TradingCost);
        without.Total.IsUnderstated.Should().BeTrue();

        with.Total.BorrowFee.Should().Be(1.64m);
        with.Total.Amount.Should().Be(with.Total.TradingCost + 1.64m);
        with.CostRatio.Should().Be(with.Total.Amount / with.TradeValueDifference);
    }

    [Fact]
    public void 借株料の未計上の件数は合計へ0として混ぜず件数で持つ()
    {
        var review = Build(BothMarkets(), borrowFees: BorrowFees(1.64m, unavailable: 2));

        review.Total.BorrowFee.Should().Be(1.64m);
        review.Total.BorrowFeeUnrecordedCount.Should().Be(2);
        review.Total.IsUnderstated.Should().BeTrue();
    }

    // FR-06, 計画 ADR-0035 決定 3, #1201: 為替スプレッドと借株料がどちらも供給されていても、未計上の借株料が 1 件でもあれば過小である。
    // 現在は為替スプレッドが常に未供給なので、この条件だけを外しても他の試験は赤にならない（独立監査の変異 M3）。
    [Fact]
    public void 全区分が供給されていても借株料の未計上が残れば過小と判定される()
    {
        new PeriodCostTotal(10m, FxSpread: 1m, BorrowFee: 2m, BorrowFeeUnrecordedCount: 1).IsUnderstated.Should().BeTrue();
        new PeriodCostTotal(10m, FxSpread: 1m, BorrowFee: 2m, BorrowFeeUnrecordedCount: 0).IsUnderstated.Should().BeFalse();
    }

    // --- 費用率（分母の 3 通り） ---

    [Fact]
    public void 分母が正なら費用率は費用合計を約定代金差額で割った値になる()
    {
        var fills = new[]
        {
            Fill(Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 0),
            Fill(Market.UnitedStates, TradeSide.Sell, 10, 1_200m, 2_880),
        };

        var review = Build(fills);

        review.TradeValueDifference.Should().Be(2_000m);
        review.CostRatio.Should().Be(review.Total.Amount / 2_000m);
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

        review.TradeValueDifference.Should().Be(-2_000m);
        review.CostRatio.Should().BeNull();
    }

    [Fact]
    public void 約定が無い期間の費用率は算出不能であり0ではない()
    {
        var review = Build([]);

        review.Total.Amount.Should().Be(0m);
        review.TradeValueDifference.Should().Be(0m);
        review.CostRatio.Should().BeNull();
    }

    // --- 概算費用関数の非破壊追加（既存の値が変わっていないこと） ---

    // #1217, IADR-0508: 事前見積りは売買方向と数量を受け取る（米国株の売りに取引諸費用）。売りの行を足した（2026-10-08）。
    [Theory]
    [InlineData(Market.Japan, TradeSide.Buy, 250_000)]
    [InlineData(Market.UnitedStates, TradeSide.Buy, 10_000)]
    [InlineData(Market.UnitedStates, TradeSide.Sell, 10_000)]
    public void 内訳版の合計は既存の概算費用関数と同値である(Market market, TradeSide side, double notional)
    {
        var amount = (decimal)notional;

        CostCalculator.EstimateOneWayCostBreakdown(Assumptions, market, side, 100, amount).Total
            .Should().Be(CostCalculator.EstimateOneWayCost(Assumptions, market, side, 100, amount));
    }
}
