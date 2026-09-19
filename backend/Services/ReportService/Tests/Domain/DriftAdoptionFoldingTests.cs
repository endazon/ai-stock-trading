using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-11, FR-16, ADR-0041 決定 1, #870, #859, IADR-0360 決定 4:
// **手動売買の取り込みは在庫だけを畳み、確定値のどれにも算入されない**ことを固定する。
//
// 🔴 本ファイルの大半は**否定形**である。取り込みが実現損益・決済件数・勝率・費用の概算・三者比較へ
// 1 円でも入ったら、それは「システム外の売買の価格を知っている」という嘘になる（価格は分からないのであって 0 ではない）。
// 併せて、畳まないと出てしまう**実在しない建玉の評価損益**（#859 の主訴）が消えることを固定する。
public class DriftAdoptionFoldingTests
{
    private const decimal TaxRate = 0.20315m;

    // 費用が 0 でない前提（取り込みが費用へ算入されないことを「0 と 0 の一致」で誤魔化さないため）。
    private static TradingAssumptions Assumptions() => new()
    {
        CapitalGainsTaxRate = TaxRate,
        JapanCommission = new CommissionSchedule(0m, 0m, 0m),
        UnitedStatesCommission = new CommissionSchedule(1m, 0m, 100m),
        FxSpreadRatio = 0m,
        MinimumExpectedProfitMultiple = 1.5m,
        CostLimits = new MonthlyCostLimits(20_000m, 15_000m, 5_000m, 0m),
    };

    private static PeriodTradeFill Buy(int qty, decimal price, int minute, BrokerProvider? provider = null) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, qty, price,
            new DateTimeOffset(2026, 9, 18, 0, minute, 0, TimeSpan.Zero), Provider: provider);

    private static PeriodTradeFill Sell(int qty, decimal price, int minute, BrokerProvider? provider = null) =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, qty, price,
            new DateTimeOffset(2026, 9, 18, 0, minute, 0, TimeSpan.Zero), Provider: provider);

    // 利用者が証券会社のアプリで売った分（数量だけ・価格は不明）。
    private static PeriodDriftAdoption Adoption(int qty, int before, int after, int minute) =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Sell, qty, before, after,
            new DateTimeOffset(2026, 9, 18, 0, minute - 1, 0, TimeSpan.Zero),
            "owner", "moomoo アプリから直接売却したため。",
            new DateTimeOffset(2026, 9, 18, 0, minute, 0, TimeSpan.Zero));

    private static readonly IReadOnlyDictionary<string, decimal> Prices =
        new Dictionary<string, decimal> { ["AAPL"] = 300m };

    // ---- 取り込みを反映しないと「実在しない建玉の評価損益」が出る（#859 の主訴） ----

    [Fact]
    public void 期間内に建てて期間外に手動で売った建玉は_取り込みを反映すると評価損益が消える()
    {
        PeriodTradeFill[] fills = [Buy(10, 200m, 0)];

        // 取り込みを照会できていない（null）＝従来どおり「決済されなかった建玉」として畳まれる。
        var withoutAdoption = PnlAggregator.Aggregate(fills, Assumptions(), Prices);
        withoutAdoption.UnrealizedPnl.Should().Be(1_000m, "取り込みが未供給なら建玉が残って見える（そのことを隠さない）");

        // 取り込みを反映すると建玉は消え、**実在しない建玉の評価損益は出ない**。
        var withAdoption = PnlAggregator.Aggregate(
            fills, Assumptions(), Prices, [Adoption(10, before: 10, after: 0, minute: 5)]);

        withAdoption.UnrealizedPnl.Should().Be(0m);
    }

    [Fact]
    public void 部分的な取り込みは_残った建玉のぶんだけ評価損益を残す()
    {
        PeriodTradeFill[] fills = [Buy(10, 200m, 0)];

        var summary = PnlAggregator.Aggregate(
            fills, Assumptions(), Prices, [Adoption(6, before: 10, after: 4, minute: 5)]);

        // 残り 4 株 × (300 − 200)。**取得単価は取り込みで動かない。**
        summary.UnrealizedPnl.Should().Be(400m);
    }

    // ---- 否定形: 確定値のどれにも入らない ----

    [Fact]
    public void 取り込みは実現損益にも決済件数にも勝ち決済にも費用にも税にも算入されない()
    {
        PeriodTradeFill[] fills = [Buy(10, 200m, 0)];

        var baseline = PnlAggregator.Aggregate(fills, Assumptions(), Prices, []);
        var adopted = PnlAggregator.Aggregate(
            fills, Assumptions(), Prices, [Adoption(10, before: 10, after: 0, minute: 5)]);

        adopted.RealizedPnlGross.Should().Be(baseline.RealizedPnlGross).And.Be(0m);
        adopted.RealizingTradeCount.Should().Be(baseline.RealizingTradeCount).And.Be(0);
        adopted.WinningTradeCount.Should().Be(baseline.WinningTradeCount).And.Be(0);
        adopted.TotalCost.Should().Be(baseline.TotalCost, "費用の概算は約定にだけ掛かる");
        adopted.TaxWithheld.Should().Be(baseline.TaxWithheld).And.Be(0m);
        adopted.RealizedPnlNet.Should().Be(baseline.RealizedPnlNet);
        adopted.TradeCount.Should().Be(1, "約定件数は約定だけを数える（取り込みは約定ではない）");
    }

    [Fact]
    public void 取り込みの後の売り約定は_取得単価が変わらないため実現損益が同じになる()
    {
        // 10 株建てて 6 株はシステム外で売られ、残り 4 株をシステムが売る。
        PeriodTradeFill[] fills = [Buy(10, 200m, 0), Sell(4, 250m, 10)];

        var summary = PnlAggregator.Aggregate(
            fills, Assumptions(), Prices, [Adoption(6, before: 10, after: 4, minute: 5)]);

        // (250 − 200) × 4。🔴 **取り込みは取得単価を動かさない**（その時点の平均取得単価で畳むため）。
        summary.RealizedPnlGross.Should().Be(200m);
        summary.RealizingTradeCount.Should().Be(1, "決済は「システムが売った 1 件」だけである");
        summary.UnrealizedPnl.Should().Be(0m);
    }

    [Fact]
    public void 帰属行は約定にだけ作られ_取り込みは行にならない()
    {
        PeriodTradeFill[] fills = [Buy(10, 200m, 0), Sell(4, 250m, 10)];

        var attributions = FillPnlAttributionBuilder.Build(
            fills, Assumptions(), rationales: null, [Adoption(6, before: 10, after: 4, minute: 5)]);

        attributions.Should().HaveCount(2, "帰属行は約定 2 件ぶんだけ");
        attributions.Select(a => a.Sequence).Should().Equal(1, 2);
        attributions.Count(a => a.Realizing).Should().Be(1);
        attributions.Single(a => a.Realizing).RealizedPnlGross.Should().Be(200m);

        // 🔴 費用の内訳（週報 §5）は帰属行だけを入力に持つため、取り込みは構造的に入らない。
        var review = PeriodCostReviewBuilder.Build(attributions, Assumptions(), taxWithheld: 0m);
        review.TotalCost.Should().Be(attributions.Sum(a => a.Cost));
    }

    [Fact]
    public void 帰属行の合計は_取り込みがあってもサマリの実現損益と一致する()
    {
        PeriodTradeFill[] fills = [Buy(10, 200m, 0), Sell(4, 250m, 10)];
        PeriodDriftAdoption[] adoptions = [Adoption(6, before: 10, after: 4, minute: 5)];

        var summary = PnlAggregator.Aggregate(fills, Assumptions(), Prices, adoptions);
        var attributions = FillPnlAttributionBuilder.Build(fills, Assumptions(), null, adoptions);

        // 🔴 畳み込みの順序と規則が 2 つの経路で一致していなければ、ここが静かにずれる（IADR-0301）。
        attributions.Sum(a => a.RealizedPnlGross).Should().Be(summary.RealizedPnlGross);
        attributions.Sum(a => a.Cost).Should().Be(summary.TotalCost);
    }

    [Fact]
    public void 取り込みは三者比較のどの段にも算入されない()
    {
        PeriodTradeFill[] fills =
        [
            Buy(10, 200m, 0, BrokerProvider.MoomooSimulate),
            Sell(4, 250m, 10, BrokerProvider.MoomooSimulate),
        ];

        var comparison = ThreeWayComparisonAggregator.Aggregate(fills, Assumptions(), TradingStage.Stage1Simulate);

        // 🔴 三者比較は**発注先**で段を分ける。取り込みは発注先が不明であり、どちらの段にも入らない。
        // その保証は値の比較ではなく**型**である——集計関数は取り込みを受け取る引数を持たない（IADR-0360 決定 4）。
        // 引数が生えたら本テストのコンパイルは通るが、下の否定形（署名の固定）が落ちる。
        typeof(ThreeWayComparisonAggregator)
            .GetMethod(nameof(ThreeWayComparisonAggregator.Aggregate))!
            .GetParameters()
            .Select(prm => prm.ParameterType)
            .Should().NotContain(typeof(IReadOnlyList<PeriodDriftAdoption>),
                "三者比較は取り込みを算入しない（発注先が不明なのでどちらの段にも寄せられない）");

        comparison!.TradeCount.Simulate.Should().Be(2, "取引件数は約定だけ");
        comparison.WinRate.Simulate.Should().Be(1m, "勝率の分母にも分子にも取り込みは入らない");
        comparison.AveragePnlUsd.Simulate.Should().NotBeNull();
        comparison.UnattributedTradeCount.Should().Be(0, "取り込みは「発注先不明の約定」として数えられない");
    }

    [Fact]
    public void 取引履歴の明細は約定だけで_取り込みは別欄へ出る()
    {
        PeriodTradeFill[] fills = [Buy(10, 200m, 0), Sell(4, 250m, 10)];
        PeriodDriftAdoption[] adoptions = [Adoption(6, before: 10, after: 4, minute: 5)];

        var view = TradeHistoryViewBuilder.Build(fills, Assumptions(), null, adoptions);

        view.Lines.Should().HaveCount(2, "§2 の明細は約定だけ（約定単価も実現損益も分からない行を混ぜない）");
        view.Lines.Select(l => l.Index).Should().Equal(1, 2);
        view.Lines.Last().RealizedPnl.Should().Be(200m);
        view.DriftAdoptions.Should().BeEquivalentTo(adoptions, "§2-b の供給元");
    }

    [Fact]
    public void 取り込みが未供給なら取引履歴も未供給として持ち回る()
    {
        var view = TradeHistoryViewBuilder.Build([Buy(10, 200m, 0)], Assumptions(), null, adoptions: null);

        // 🔴 null（照会できていない）を空列（該当なし）へ潰さない。
        view.DriftAdoptions.Should().BeNull();
    }

    // ---- 為替差損益: 実在しない建玉の期末再測定を止める。明細は作らない（レートが知り得ない） ----

    [Fact]
    public void 取り込みで消えた建玉は期末レートで再測定されない()
    {
        PeriodTradeFill[] fills =
        [
            Buy(10, 200m, 0) with { FxRateBaseToDisplay = 150m },
        ];
        var periodEnd = new PeriodEndFxRate(160m, new DateOnly(2026, 9, 18));

        var withoutAdoption = FxTranslationBuilder.Build(fills, periodEnd);
        withoutAdoption.Summary!.TranslationGainJpy.Should().NotBe(0m, "取り込みが未供給なら建玉が残って再測定される");

        var withAdoption = FxTranslationBuilder.Build(
            fills, periodEnd, [Adoption(10, before: 10, after: 0, minute: 5)]);

        // 🔴 **決済時の認識時レートは知り得ない**ため明細を作らず、期末の再測定も消える（0 円・明細 0 件）。
        withAdoption.Summary.Should().NotBeNull();
        withAdoption.Summary!.TranslationGainJpy.Should().Be(0m);
        withAdoption.UnrecordedFillCount.Should().Be(0);
    }

    // ---- 畳み込みの順序 ----

    [Fact]
    public void 同時刻では約定を先に畳む()
    {
        // 同じ瞬間に「買い 10」と「取り込み（10 → 0）」がある。約定が先でなければ在庫が合わない。
        PeriodTradeFill[] fills = [Buy(10, 200m, 5)];
        var merged = PeriodLedgerTimeline.Merge(fills, [Adoption(10, before: 10, after: 0, minute: 5)]);

        merged.Should().HaveCount(2);
        merged[0].Fill.Should().NotBeNull();
        merged[1].Adoption.Should().NotBeNull();
    }
}
