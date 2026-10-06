using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using ReportService.Domain;
using ReportService.Features.Reports;
using Xunit;

namespace ReportService.Tests;

// T-06-040〜T-06-045, FR-06, FR-16, #1181, IADR-0493 決定 3・4: 期間開始時点の在庫を初期在庫に置いた畳み込み。
// 期待値は**平均取得単価法の手計算**である（コードの出力を写していない）。前提条件は既定（手数料・為替スプレッド 0・譲渡益税率 20.315%）。
public class OpeningInventoryAggregationTests
{
    private static readonly TradingAssumptions Assumptions = TradingAssumptionsDefaults.Create();

    // ET 2026-10-05 のセッション（EDT・UTC−4）。
    private static DateTimeOffset Et(int hour, int minute) => new(2026, 10, 5, hour + 4, minute, 0, TimeSpan.Zero);

    private static PeriodTradeFill Sell(string symbol, int quantity, decimal price, DateTimeOffset at, decimal? rate = null) =>
        new(symbol, Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, quantity, price, at, FxRateBaseToDisplay: rate);

    private static PeriodTradeFill Buy(string symbol, int quantity, decimal price, DateTimeOffset at, decimal? rate = null) =>
        new(symbol, Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, quantity, price, at, FxRateBaseToDisplay: rate);

    private static OpeningLot Long(string symbol, int quantity, decimal averageCost, decimal? rate = 150m, int unrecorded = 0) =>
        new(symbol, Market.UnitedStates, quantity, averageCost, rate, unrecorded);

    // issue #1181 の観測: MSFT 468 株（平均取得単価 511.912）・NVDA 1049 株（230.77）を前期に建て、ET 10/05 に全量決済した。
    private static readonly OpeningInventorySnapshot IssueOpening = new([Long("MSFT", 468, 511.912m), Long("NVDA", 1049, 230.77m)]);

    private static readonly PeriodTradeFill[] IssueFills =
    [
        Sell("MSFT", 468, 527.15m, Et(10, 30)),
        Sell("NVDA", 1049, 237.69m, Et(11, 0)),
    ];

    // ---- T-06-040: 前期に建てた建玉の全量決済（再現・手計算） ----

    [Fact]
    public void T06_040_前期に建てた_MSFT_と_NVDA_の決済の実現損益が手計算と一致する()
    {
        var pnl = PnlAggregator.Aggregate(IssueFills, Assumptions, opening: IssueOpening);

        // MSFT (527.15 − 511.912) × 468 = 7,131.384 ／ NVDA (237.69 − 230.77) × 1,049 = 7,259.08 → 計 14,390.464。
        pnl.RealizedPnlGross.Should().Be(14_390.464m);
        pnl.TotalCost.Should().Be(0m);
        // 税 = 14,390.464 × 0.20315 = 2,923.4227616 ／ 税引後 = 11,467.0412384。
        pnl.TaxWithheld.Should().Be(2_923.4227616m);
        pnl.RealizedPnlNet.Should().Be(11_467.0412384m);
        pnl.RealizingTradeCount.Should().Be(2);
        pnl.WinningTradeCount.Should().Be(2);
        pnl.UnvaluedSettlementCount.Should().Be(0, "期間開始時点の在庫で賄える＝算定できる");
        pnl.IsPartial.Should().BeFalse();
        pnl.UnrealizedPnl.Should().Be(0m, "全量を決済したので期末に建玉は残らない");
    }

    [Fact]
    public void T06_040_在庫を受け取らなければ従来どおり算定できない決済として数える()
    {
        var pnl = PnlAggregator.Aggregate(IssueFills, Assumptions);

        pnl.UnvaluedSettlementCount.Should().Be(2);
        pnl.RealizedPnlGross.Should().Be(0m);
        pnl.IsPartial.Should().BeTrue();
    }

    // ---- T-06-041: 一部決済 2 回と建て増しを挟む決済（平均取得単価法） ----

    [Fact]
    public void T06_041_一部決済と建て増しを挟む決済の実現損益が平均取得単価法の手計算と一致する()
    {
        var opening = new OpeningInventorySnapshot([Long("MSFT", 400, 500m)]);
        PeriodTradeFill[] fills =
        [
            Sell("MSFT", 200, 520m, Et(9, 45)),   // (520 − 500) × 200 = 4,000。残 200 @500
            Buy("MSFT", 200, 530m, Et(10, 0)),    // 平均 (200×500 + 200×530) / 400 = 515
            Sell("MSFT", 400, 527m, Et(15, 0)),   // (527 − 515) × 400 = 4,800
        ];

        var pnl = PnlAggregator.Aggregate(fills, Assumptions, opening: opening);

        pnl.RealizedPnlGross.Should().Be(8_800m);
        pnl.RealizingTradeCount.Should().Be(2);
        pnl.UnvaluedSettlementCount.Should().Be(0);
    }

    // ---- T-06-042: 持ち越して当期に決済しない建玉の評価損益 ----

    [Fact]
    public void T06_042_持ち越した建玉は評価損益に入る()
    {
        var opening = new OpeningInventorySnapshot([Long("AAPL", 100, 200m), Long("MSFT", 10, 500m)]);
        PeriodTradeFill[] fills = [Sell("MSFT", 10, 510m, Et(10, 0))];

        var pnl = PnlAggregator.Aggregate(
            fills, Assumptions, new Dictionary<string, decimal> { ["AAPL"] = 210m, ["MSFT"] = 999m }, opening: opening);

        pnl.UnrealizedPnl.Should().Be(1_000m, "AAPL (210 − 200) × 100。決済済みの MSFT は評価しない");
        pnl.RealizedPnlGross.Should().Be(100m);
    }

    [Fact]
    public async Task T06_042_約定が無い日でも持ち越した建玉の現在値を市場データ源から引く()
    {
        var market = new StubMarketData(new() { [("AAPL", Market.UnitedStates)] = 210m });
        var sut = new ReportDraftService(new FakeDrafter(), market);

        var draft = await sut.BuildDraftAsync(new DraftRequest(
            ReportKind.Daily, "daily-2026-10-06", new DateOnly(2026, 10, 6), ["US"], 1, null, "方針", [], null,
            OpeningInventory: new OpeningInventorySnapshot([Long("AAPL", 100, 200m)])));

        market.Requested.Should().Equal([("AAPL", Market.UnitedStates)]);
        draft.Pnl.UnrealizedPnl.Should().Be(1_000m);
    }

    // ---- T-06-043: 期間開始時点の在庫と期間の買いを超える売りは、従来どおり算定できないと数える ----

    [Fact]
    public void T06_043_在庫と期間の買いを超える手仕舞いは算定できない決済として数える()
    {
        var opening = new OpeningInventorySnapshot([Long("MSFT", 100, 500m)]);
        PeriodTradeFill[] fills = [Buy("MSFT", 20, 520m, Et(9, 45)), Sell("MSFT", 150, 530m, Et(15, 0))];

        var pnl = PnlAggregator.Aggregate(fills, Assumptions, new Dictionary<string, decimal> { ["MSFT"] = 600m }, opening: opening);

        pnl.UnvaluedSettlementCount.Should().Be(1, "賄えない 30 株を黙って落とさない");
        pnl.IsPartial.Should().BeTrue();
        // 賄えた 120 株だけを決済する（平均 (100×500 + 20×520) / 120 = 503.33… の部分値）。幻のショートは開かない。
        pnl.UnrealizedPnl.Should().Be(0m, "在庫は 0 でクランプされ幻のショートを評価しない");
    }

    // ---- T-06-044: 内訳・明細の合計が §1 と一致する（同じ初期在庫） ----

    [Fact]
    public void T06_044_帰属と明細の実現損益の合計が_PnlAggregator_と一致する()
    {
        var opening = new OpeningInventorySnapshot([Long("MSFT", 400, 500m), Long("NVDA", 1049, 230.77m)]);
        PeriodTradeFill[] fills =
        [
            Sell("MSFT", 200, 520m, Et(9, 45)),
            Buy("MSFT", 200, 530m, Et(10, 0)),
            Sell("NVDA", 1049, 237.69m, Et(11, 0)),
            Sell("MSFT", 400, 527m, Et(15, 0)),
        ];

        var pnl = PnlAggregator.Aggregate(fills, Assumptions, opening: opening);
        var attributions = FillPnlAttributionBuilder.Build(fills, Assumptions, rationales: null, opening: opening);
        var history = TradeHistoryViewBuilder.Build(fills, Assumptions, rationales: null, opening: opening);

        pnl.RealizedPnlGross.Should().Be(8_800m + 7_259.08m);
        attributions.Sum(a => a.RealizedPnlGross).Should().Be(pnl.RealizedPnlGross);
        attributions.Should().OnlyContain(a => !a.Unvalued);
        history.Lines.Sum(l => l.RealizedPnl).Should().Be(pnl.RealizedPnlGross);
        history.Lines.Should().OnlyContain(l => !l.RealizedPnlUnvalued);
    }

    // ---- T-06-045: 為替差損益は持ち越した建玉の認識時レートから再測定する ----

    [Fact]
    public void T06_045_持ち越した建玉の決済は認識時レートの加重平均から決済時レートへ再測定する()
    {
        var opening = new OpeningInventorySnapshot([Long("MSFT", 100, 500m, rate: 150m)]);
        PeriodTradeFill[] fills = [Sell("MSFT", 100, 520m, Et(10, 0), rate: 155m)];

        var result = FxTranslationBuilder.Build(fills, periodEnd: null, opening: opening);

        // USD 原価 100 × 500 = 50,000 を 150 → 155 円/ドルで再測定: 50,000 × 5 = +250,000 円。期末に建玉は残らない。
        result.UnrecordedFillCount.Should().Be(0);
        result.Summary!.TranslationGainJpy.Should().Be(250_000m);
        result.Summary.EntryCount.Should().Be(1);
    }

    [Fact]
    public void T06_045_認識時レートが未記録の持ち越しは件数に足して未供給にする()
    {
        var opening = new OpeningInventorySnapshot([Long("MSFT", 100, 500m, rate: null, unrecorded: 2)]);
        PeriodTradeFill[] fills = [Sell("MSFT", 100, 520m, Et(10, 0), rate: 155m)];

        var result = FxTranslationBuilder.Build(fills, periodEnd: null, opening: opening);

        result.Summary.Should().BeNull("推定で埋めない（0 円と書かない）");
        result.UnrecordedFillCount.Should().Be(2);
    }

    [Fact]
    public void T06_045_持ち越して期末に残る建玉は期末レートが無ければ未供給()
    {
        var opening = new OpeningInventorySnapshot([Long("MSFT", 100, 500m, rate: 150m)]);

        var result = FxTranslationBuilder.Build([], periodEnd: null, opening: opening);

        result.Summary.Should().BeNull("期末に建玉が残るのに期末レートが無い");
        result.UnrecordedFillCount.Should().Be(0);
    }

    // ---- T-06-051（描画・要約・散文）: 照会できなかったときは取得原価を要する値を算出不能にする ----

    [Fact]
    public void T06_051_在庫を照会できなかった期間は要約と散文の文脈に値を出さない()
    {
        var pnl = PnlAggregator.Aggregate([Buy("MSFT", 10, 500m, Et(10, 0))], Assumptions) with
        {
            OpeningInventoryUnknown = true,
        };

        pnl.IsPartial.Should().BeTrue();
        ReportSummary.Build(ReportKind.Daily, "2026-10-06", pnl, "散文")
            .Should().Contain("算出不能（期間開始時点の在庫を照会できませんでした）")
            .And.Contain("決済・勝ちは算出不能");

        var prompt = ReportNarrativePromptBuilder.Build(new ReportNarrativeContext(
            ReportKind.Daily, "daily-2026-10-06", "2026-10-06", ["US"], pnl, "方針", null));
        prompt.Should().Contain("- 実現損益(税引後): 算出不能")
            .And.Contain("- 評価損益(参考): 算出不能")
            .And.Contain("期間開始時点の在庫を照会できなかった");
    }

    private sealed class FakeDrafter : IReportNarrativeDrafter
    {
        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult("散文");
    }

    private sealed class StubMarketData(Dictionary<(string Symbol, Market Market), decimal> prices) : IMarketDataSource
    {
        public List<(string Symbol, Market Market)> Requested { get; } = [];

        public Task<Quote?> GetLatestQuoteAsync(string symbol, Market market, CancellationToken cancellationToken = default)
        {
            Requested.Add((symbol, market));
            return Task.FromResult(prices.TryGetValue((symbol, market), out var price)
                ? new Quote(symbol, market, price, DateTimeOffset.UnixEpoch)
                : null);
        }
    }
}
