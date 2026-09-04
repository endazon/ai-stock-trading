using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-07, FR-16, #615, IADR-0301: 約定単位の損益帰属（週報 §2/§3 の単一情報源）の検証。
//
// 🔴 **本テストの中核は「内訳の和が §1 サマリの合計と一致する」ことである。**
// これは畳み込みが期間全体で 1 回だけ行われていることの証跡であり、期間を切って
// `PnlAggregator.Aggregate` を呼び直す実装では成立しない（持ち越し建玉の平均取得単価がスライス内に無いため）。
public class FillPnlAttributionTests
{
    // 2026-08-24 は月曜。T0 = JST 09:05。
    private static readonly DateTimeOffset T0 = new(2026, 8, 24, 0, 5, 0, TimeSpan.Zero);

    // 🔴 **手数料を 0 でない値にする。** 既定の前提条件は手数料 0 であり、それでは
    // 「費用の和が §1 と一致する」検査が 0 == 0 になって何も固定しない。
    private static TradingAssumptions Assumptions() => new()
    {
        CapitalGainsTaxRate = 0.20315m,
        JapanCommission = new CommissionSchedule(0.001m, 0m, 0m),
        UnitedStatesCommission = new CommissionSchedule(0.001m, 0m, 0m),
        FxSpreadRatio = 0m,
        MinimumExpectedProfitMultiple = 1.5m,
        CostLimits = new MonthlyCostLimits(20_000m, 15_000m, 5_000m, 0m),
    };

    private static PeriodTradeFill Fill(
        TradeSide side, int quantity, decimal price, int minutes, Guid decisionId = default,
        Market market = Market.Japan, string symbol = "7203") =>
        new(symbol, market, side,
            side == TradeSide.Buy ? PositionEffect.Open : PositionEffect.Close,
            quantity, price, T0.AddMinutes(minutes), decisionId);

    // 週をまたいで建てた玉を期間内で決済する形（**期間を切って畳み込み直すと壊れる唯一の場所**）。
    private static IReadOnlyList<PeriodTradeFill> CarryOverWeek() =>
    [
        // 月曜: JP を建てる（この日は決済なし）。
        Fill(TradeSide.Buy, 100, 2_500m, 0),
        // 火曜: US を建てる。
        Fill(TradeSide.Buy, 10, 290m, 1_440, market: Market.UnitedStates, symbol: "AAPL"),
        // 水曜: US を利確（建値は火曜の玉＝スライスすると参照できない）。
        Fill(TradeSide.Sell, 10, 315m, 2_880, market: Market.UnitedStates, symbol: "AAPL"),
        // 木曜: JP を損切り（建値は月曜の玉＝同上）。
        Fill(TradeSide.Sell, 100, 2_480m, 4_320),
    ];

    // --- 🔴 内訳の和が §1 サマリの合計と一致する（畳み込みが 1 回であることの証跡） ---

    [Fact]
    public void 約定単位の帰属の和がサマリの合計と一致する()
    {
        var fills = CarryOverWeek();
        var assumptions = Assumptions();

        var entries = FillPnlAttributionBuilder.Build(fills, assumptions, rationales: null);
        var summary = PnlAggregator.Aggregate(fills, assumptions);

        entries.Sum(e => e.RealizedPnlGross).Should().Be(summary.RealizedPnlGross);
        entries.Sum(e => e.Cost).Should().Be(summary.TotalCost);
        entries.Count.Should().Be(summary.TradeCount);
        entries.Count(e => e.Realizing).Should().Be(summary.RealizingTradeCount);
        entries.Count(e => e.Realizing && e.RealizedPnlGross > 0m).Should().Be(summary.WinningTradeCount);
    }

    [Fact]
    public void 日別集計の和もサマリの合計と一致する()
    {
        var fills = CarryOverWeek();
        var assumptions = Assumptions();

        var rows = FillPnlAttributionBuilder.ByDay(
            FillPnlAttributionBuilder.Build(fills, assumptions, rationales: null));
        var summary = PnlAggregator.Aggregate(fills, assumptions);

        // グルーピングで行が落ちても捕まえる（帰属の和だけでは検知できない）。
        rows.Sum(r => r.RealizedPnlGross).Should().Be(summary.RealizedPnlGross);
        rows.Sum(r => r.Cost).Should().Be(summary.TotalCost);
        rows.Sum(r => r.FillCount).Should().Be(summary.TradeCount);
        rows.Sum(r => r.RealizingCount).Should().Be(summary.RealizingTradeCount);

        // 週報 §2 が出す値（税引前・費用込み）の和は、サマリの税引後実現損益に税額を足したものである。
        rows.Sum(r => r.RealizedPnlAfterCost).Should().Be(summary.RealizedPnlNet + summary.TaxWithheld);
    }

    // 🔴 **この実装で採らなかった形（期間を切って集計器を呼び直す）が、なぜ壊れるかを固定する。**
    // 壊れ方は「例外」でも「赤いテスト」でもなく、**数値が静かに変わる**ことである。
    [Fact]
    public void 期間を切って集計器を呼び直すと持ち越し建玉の分だけ合計がずれる()
    {
        var fills = CarryOverWeek();
        var assumptions = Assumptions();

        var wholePeriod = PnlAggregator.Aggregate(fills, assumptions);
        var slicedSum = fills
            .GroupBy(f => DateOnly.FromDateTime(f.ExecutedAt.ToOffset(TimeSpan.FromHours(9)).DateTime))
            .Sum(g => PnlAggregator.Aggregate([.. g], assumptions).RealizedPnlGross);

        slicedSum.Should().NotBe(wholePeriod.RealizedPnlGross,
            "日で切って畳み込み直すと、持ち越し建玉の平均取得単価がスライス内に存在しないため実現損益が消える");

        // 帰属からの集計は一致する（＝スライスしていない）。
        FillPnlAttributionBuilder.Build(fills, assumptions, null)
            .Sum(e => e.RealizedPnlGross).Should().Be(wholePeriod.RealizedPnlGross);
    }

    // --- 帰属の粒度（日・週・市場・方向のいずれにも集計できること） ---

    [Fact]
    public void 帰属日をJSTで決める()
    {
        // JST 09:05 の約定は同じ日、JST 翌 00:05（UTC 15:05）の約定は翌日へ帰属する。
        var entries = FillPnlAttributionBuilder.Build(
            [Fill(TradeSide.Buy, 100, 2_500m, 0), Fill(TradeSide.Buy, 100, 2_500m, 900)],
            Assumptions(), null);

        entries.Select(e => e.SessionDateJst).Should().Equal(
            new DateOnly(2026, 8, 24), new DateOnly(2026, 8, 25));
    }

    [Fact]
    public void 決済でない約定の実現損益は0であり未供給ではない()
    {
        var assumptions = Assumptions();
        var fill = Fill(TradeSide.Buy, 10, 290m, 0, market: Market.UnitedStates, symbol: "AAPL");
        var entries = FillPnlAttributionBuilder.Build([fill], assumptions, null);

        var entry = entries.Should().ContainSingle().Subject;
        entry.Realizing.Should().BeFalse();
        entry.RealizedPnlGross.Should().Be(0m);
        // 費用は新規建てにも掛かり、**PnlAggregator と同じ関数**で積む（片方だけ変わる余地を残さない）。
        entry.Cost.Should().Be(
            CostCalculator.EstimateOneWayCost(assumptions, Market.UnitedStates, fill.Quantity * fill.Price));
        entry.Cost.Should().BeGreaterThan(0m);
    }

    [Fact]
    public void 方向別の集計キーは決済の売買方向から一意に決まる()
    {
        // ロングを建てて決済（Sell が決済）＋ショートを建てて決済（Buy が決済）。
        var entries = FillPnlAttributionBuilder.Build(
        [
            Fill(TradeSide.Buy, 100, 2_500m, 0),
            Fill(TradeSide.Sell, 100, 2_600m, 60),
            Fill(TradeSide.Sell, 10, 300m, 120, market: Market.UnitedStates, symbol: "AAPL"),
            Fill(TradeSide.Buy, 10, 280m, 180, market: Market.UnitedStates, symbol: "AAPL"),
        ], Assumptions(), null);

        // 決済のうち Sell はロングの決済、Buy はショートの決済である（再畳み込み不要）。
        entries.Where(e => e.Realizing && e.Side == TradeSide.Sell).Should().ContainSingle()
            .Which.RealizedPnlGross.Should().Be(10_000m); // (2600-2500)*100
        entries.Where(e => e.Realizing && e.Side == TradeSide.Buy).Should().ContainSingle()
            .Which.RealizedPnlGross.Should().Be(200m); // (300-280)*10
    }

    [Fact]
    public void 市場別に集計できる()
    {
        var entries = FillPnlAttributionBuilder.Build(CarryOverWeek(), Assumptions(), null);

        entries.Count(e => e.Market == Market.Japan).Should().Be(2);
        entries.Count(e => e.Market == Market.UnitedStates).Should().Be(2);
    }

    // --- 日別集計 ---

    [Fact]
    public void 約定が1件も無い日は行を出さない()
    {
        // 月曜と木曜にだけ約定がある期間（火曜・水曜は行そのものが存在しない）。
        var rows = FillPnlAttributionBuilder.ByDay(FillPnlAttributionBuilder.Build(
            [Fill(TradeSide.Buy, 100, 2_500m, 0), Fill(TradeSide.Sell, 100, 2_600m, 4_320)],
            Assumptions(), null));

        rows.Select(r => r.SessionDateJst).Should().Equal(new DateOnly(2026, 8, 24), new DateOnly(2026, 8, 27));
    }

    [Fact]
    public void 決済が無い日の寄与最大は空になる()
    {
        var rows = FillPnlAttributionBuilder.ByDay(
            FillPnlAttributionBuilder.Build([Fill(TradeSide.Buy, 100, 2_500m, 0)], Assumptions(), null));

        rows.Should().ContainSingle().Which.LargestContributor.Should().BeNull();
    }

    [Fact]
    public void 寄与最大は実現損益の絶対値が最大の決済を選ぶ()
    {
        // 同日に「+小さな利確」と「−大きな損切り」がある日は、損切りのほうが寄与が大きい。
        var rows = FillPnlAttributionBuilder.ByDay(FillPnlAttributionBuilder.Build(
        [
            Fill(TradeSide.Buy, 100, 2_500m, 0),
            Fill(TradeSide.Sell, 100, 2_510m, 60),
            Fill(TradeSide.Buy, 10, 300m, 120, market: Market.UnitedStates, symbol: "AAPL"),
            Fill(TradeSide.Sell, 10, 100m, 180, market: Market.UnitedStates, symbol: "AAPL"),
        ], Assumptions(), null));

        rows.Should().ContainSingle().Which.LargestContributor!.Symbol.Should().Be("AAPL");
    }

    // --- ハイライト（同値時の決定規則） ---

    [Fact]
    public void 決済が0件ならハイライトは両方とも空になる()
    {
        var highlights = FillPnlAttributionBuilder.Highlights(
            FillPnlAttributionBuilder.Build([Fill(TradeSide.Buy, 100, 2_500m, 0)], Assumptions(), null));

        highlights.Best.Should().BeNull();
        highlights.Worst.Should().BeNull();
    }

    [Fact]
    public void 最良と最悪を決済の中から選ぶ()
    {
        var highlights = FillPnlAttributionBuilder.Highlights(
            FillPnlAttributionBuilder.Build(CarryOverWeek(), Assumptions(), null));

        highlights.Best!.Symbol.Should().Be("AAPL");
        highlights.Best.RealizedPnlGross.Should().Be(250m); // (315-290)*10
        highlights.Worst!.Symbol.Should().Be("7203");
        highlights.Worst.RealizedPnlGross.Should().Be(-2_000m); // (2480-2500)*100
    }

    [Fact]
    public void 実現損益が同値でも入力の並び順でハイライトが変わらない()
    {
        // 同額の決済を 2 件持つ期間（銘柄も市場も違う）。入力の順序だけを入れ替える。
        var forward = new List<PeriodTradeFill>
        {
            Fill(TradeSide.Buy, 100, 2_500m, 0),
            Fill(TradeSide.Sell, 100, 2_600m, 60),
            Fill(TradeSide.Buy, 100, 1_000m, 120, market: Market.UnitedStates, symbol: "AAPL"),
            Fill(TradeSide.Sell, 100, 1_100m, 180, market: Market.UnitedStates, symbol: "AAPL"),
        };
        var reversed = Enumerable.Reverse(forward).ToList();

        var a = FillPnlAttributionBuilder.Highlights(FillPnlAttributionBuilder.Build(forward, Assumptions(), null));
        var b = FillPnlAttributionBuilder.Highlights(FillPnlAttributionBuilder.Build(reversed, Assumptions(), null));

        // 同値は「約定時刻の早い順」で決まる（＝先に決済した 7203）。
        a.Best!.Symbol.Should().Be("7203");
        b.Best!.Symbol.Should().Be(a.Best.Symbol);
        b.Worst!.Symbol.Should().Be(a.Worst!.Symbol);
    }

    [Fact]
    public void 同時刻で同値なら銘柄コードの序数順で決まる()
    {
        // 約定時刻まで同じ 2 件（時刻では決まらない）。銘柄コードの序数比較で一意になる。
        var entries = FillPnlAttributionBuilder.Build(
        [
            Fill(TradeSide.Buy, 100, 1_000m, 0, market: Market.UnitedStates, symbol: "ZZZ"),
            Fill(TradeSide.Buy, 100, 1_000m, 0, market: Market.UnitedStates, symbol: "AAA"),
            Fill(TradeSide.Sell, 100, 1_100m, 60, market: Market.UnitedStates, symbol: "ZZZ"),
            Fill(TradeSide.Sell, 100, 1_100m, 60, market: Market.UnitedStates, symbol: "AAA"),
        ], Assumptions(), null);

        FillPnlAttributionBuilder.Highlights(entries).Best!.Symbol.Should().Be("AAA");
    }

    [Fact]
    public void 決済が1件だけなら最良と最悪は同一の約定になる()
    {
        var highlights = FillPnlAttributionBuilder.Highlights(FillPnlAttributionBuilder.Build(
            [Fill(TradeSide.Buy, 100, 2_500m, 0), Fill(TradeSide.Sell, 100, 2_600m, 60)],
            Assumptions(), null));

        highlights.Best!.Sequence.Should().Be(highlights.Worst!.Sequence);
    }

    // --- 判断根拠は記録の転記（報告書生成時に文章を作らない） ---

    [Fact]
    public void 記録された判断根拠をそのまま転記する()
    {
        var decisionId = Guid.NewGuid();
        var entries = FillPnlAttributionBuilder.Build(
            [Fill(TradeSide.Buy, 100, 2_500m, 0, decisionId)],
            Assumptions(),
            new Dictionary<Guid, string> { [decisionId] = "始値が支持線で反発。出来高増。" });

        entries.Should().ContainSingle().Which.Rationale.Should().Be("始値が支持線で反発。出来高増。");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 相関できない約定と記録が無い場合は判断根拠を未供給にする(bool hasDictionary)
    {
        var entries = FillPnlAttributionBuilder.Build(
            [Fill(TradeSide.Buy, 100, 2_500m, 0)], // DecisionId は既定（相関できない）
            Assumptions(),
            hasDictionary ? new Dictionary<Guid, string> { [Guid.NewGuid()] = "別の取引の根拠" } : null);

        entries.Should().ContainSingle().Which.Rationale.Should().BeNull();
    }

    [Fact]
    public void 空文字の判断根拠は未供給にする()
    {
        var decisionId = Guid.NewGuid();
        var entries = FillPnlAttributionBuilder.Build(
            [Fill(TradeSide.Buy, 100, 2_500m, 0, decisionId)],
            Assumptions(),
            new Dictionary<Guid, string> { [decisionId] = "   " });

        entries.Should().ContainSingle().Which.Rationale.Should().BeNull();
    }

    [Fact]
    public void 約定が0件なら帰属も0件になる()
    {
        FillPnlAttributionBuilder.Build([], Assumptions(), null).Should().BeEmpty();
        FillPnlAttributionBuilder.ByDay([]).Should().BeEmpty();
    }
}
