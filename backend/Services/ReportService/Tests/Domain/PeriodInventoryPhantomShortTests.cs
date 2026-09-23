using ReportService.Domain;
using ReportService.Features.Reports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-11, FR-16, #892, IADR-0033, IADR-0301, IADR-0360, IADR-0381:
// **期間で切った在庫が、期間より前に建てた建玉の決済で幻のショートを作らない**ことを固定する。
//
// 🔴 本ファイルは #892 の実測（issue 本文の 2 例）をそのまま写している。是正前はいずれも
// **実在しない建玉の評価損益**を出していた（取り込みの有無に関わらず再現した）。
//
// テスト ID は **T-16-001〜014**（本作業で新設した帯。走査の結果、本リポジトリに `T-16` 帯は
// 1 件も存在しなかった。作業仕様書 `20260923_892_period-scoped-inventory-explicit-unknown` 参照）。
public class PeriodInventoryPhantomShortTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);

    private static TradingAssumptions Assumptions() => new()
    {
        CapitalGainsTaxRate = 0.20315m,
        JapanCommission = new CommissionSchedule(0m, 0m, 0m),
        UnitedStatesCommission = new CommissionSchedule(0m, 0m, 0m),
        FxSpreadRatio = 0m,
        MinimumExpectedProfitMultiple = 1.5m,
        CostLimits = new MonthlyCostLimits(20_000m, 15_000m, 5_000m, 0m),
    };

    // 🔴 `effect` は**常に明示する**（既定へ倒すと、ショートの建てが「在庫 0 への手仕舞い」に見える）。
    private static PeriodTradeFill Fill(
        TradeSide side, PositionEffect effect, int quantity, decimal price, int minutes,
        string symbol = "AAPL", Market market = Market.UnitedStates) =>
        new(symbol, market, side, effect, quantity, price, T0.AddMinutes(minutes));

    private static PeriodDriftAdoption Adoption(
        TradeSide side, int quantity, int ledgerBefore, int broker, int minutes,
        string symbol = "AAPL", Market market = Market.UnitedStates) =>
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), symbol, market, side, quantity,
            ledgerBefore, broker, T0.AddMinutes(minutes), "owner", "手動で売却", T0.AddMinutes(minutes));

    // --- T-16-001 / T-16-002: issue 実測 1（取り込みが 1 件も無い場合） ---

    // #892 実測 1: `PnlAggregator.Aggregate([Sell(100 株 @250)], 前提条件, 現在値 { AAPL: 300 })`
    // 是正前 → `unrealized=-5000 realized=0 realizing=0`（**−5,000 の幻のショート**）。
    [Fact]
    public void T16_001_期間前に建てた建玉の決済は幻のショートを開かない()
    {
        var fills = new[] { Fill(TradeSide.Sell, PositionEffect.Close, 100, 250m, 0) };
        var prices = new Dictionary<string, decimal> { ["AAPL"] = 300m };

        var s = PnlAggregator.Aggregate(fills, Assumptions(), prices);

        // 真値は「当期の在庫は残っていない」＝評価損益 0（issue 本文の明文）。
        s.UnrealizedPnl.Should().Be(0m);
    }

    [Fact]
    public void T16_002_期間前に建てた建玉の決済は実現損益を捏造せず算定できない決済として数える()
    {
        var fills = new[] { Fill(TradeSide.Sell, PositionEffect.Close, 100, 250m, 0) };

        var s = PnlAggregator.Aggregate(fills, Assumptions(), currentPrices: null);

        s.RealizedPnlGross.Should().Be(0m);
        s.RealizingTradeCount.Should().Be(0);
        s.WinningTradeCount.Should().Be(0);
        // 🔴 **黙って 0 にしない。** 「算定できなかった決済が 1 件ある」ことが報告書へ伝わる。
        s.UnvaluedSettlementCount.Should().Be(1);
    }

    // --- T-16-003: 正当な新規ショートを巻き添えにしない ---

    [Fact]
    public void T16_003_在庫ゼロへの新規建ての売りは従来どおりショートを建てる()
    {
        // `Open` ＝当期に始めた建玉。期間の在庫に無いのは「期間より前に建てたから」ではない。
        var fills = new[] { Fill(TradeSide.Sell, PositionEffect.Open, 100, 250m, 0) };
        var prices = new Dictionary<string, decimal> { ["AAPL"] = 300m };

        var s = PnlAggregator.Aggregate(fills, Assumptions(), prices);

        s.UnrealizedPnl.Should().Be(-5_000m);          // (300-250)*(-100)
        s.UnvaluedSettlementCount.Should().Be(0);
    }

    // --- T-16-004: issue 実測 2（取り込みを挟む場合） ---

    // #892 実測 2: 買い 10 @200 (t0) → 取り込み 110→100 (t20) → 売り 10 @250 (t40)、現在値 300。
    // 是正前 → `unrealized=-500`（取り込みで当期の在庫が消え、続く売りが幻のショートになる）。
    [Fact]
    public void T16_004_取り込みで当期の在庫が消えた後の決済も幻のショートを開かない()
    {
        var fills = new[]
        {
            Fill(TradeSide.Buy, PositionEffect.Open, 10, 200m, 0),
            Fill(TradeSide.Sell, PositionEffect.Close, 10, 250m, 40),
        };
        var adoptions = new[] { Adoption(TradeSide.Sell, 10, ledgerBefore: 110, broker: 100, minutes: 20) };
        var prices = new Dictionary<string, decimal> { ["AAPL"] = 300m };

        var s = PnlAggregator.Aggregate(fills, Assumptions(), prices, adoptions);

        s.UnrealizedPnl.Should().Be(0m);
        s.UnvaluedSettlementCount.Should().Be(1);
    }

    // --- T-16-005 / T-16-007: 退行が無いこと ---

    [Fact]
    public void T16_005_期間内で建てて期間内で決済した取引は従来どおり集計される()
    {
        var fills = new[]
        {
            Fill(TradeSide.Buy, PositionEffect.Open, 10, 200m, 0),
            Fill(TradeSide.Sell, PositionEffect.Close, 10, 250m, 40),
        };

        var s = PnlAggregator.Aggregate(fills, Assumptions(), currentPrices: null);

        s.RealizedPnlGross.Should().Be(500m);
        s.RealizingTradeCount.Should().Be(1);
        s.WinningTradeCount.Should().Be(1);
        s.UnvaluedSettlementCount.Should().Be(0);
    }

    [Fact]
    public void T16_007_算定できない決済でも約定件数と費用は従来どおり計上する()
    {
        // 費用は約定ごとに掛かり、**取得原価を要しない**（issue 本文「費用の概算は約定ごとに掛かるため影響しない」）。
        var assumptions = Assumptions() with
        {
            UnitedStatesCommission = new CommissionSchedule(0.001m, 0m, 0m),
        };
        var fills = new[] { Fill(TradeSide.Sell, PositionEffect.Close, 100, 250m, 0) };

        var s = PnlAggregator.Aggregate(fills, assumptions, currentPrices: null);

        s.TradeCount.Should().Be(1);
        s.TotalCost.Should().Be(
            CostCalculator.EstimateOneWayCost(assumptions, Market.UnitedStates, 100 * 250m));
        s.TotalCost.Should().BeGreaterThan(0m);
    }

    // --- T-16-008: 規則そのもの（境界） ---

    [Theory]
    // 新規建ては何があっても算定できる（在庫 0 でも建て増しでも）。
    [InlineData(0, PositionEffect.Open, -100, 0)]
    [InlineData(50, PositionEffect.Open, 100, 0)]
    [InlineData(-50, PositionEffect.Open, -100, 0)]
    // 手仕舞いは、期間の在庫で賄えない分だけが算定できない。
    [InlineData(0, PositionEffect.Close, -100, 100)]      // 在庫なし＝全量
    [InlineData(5, PositionEffect.Close, -10, 5)]         // 不足＝差分
    [InlineData(10, PositionEffect.Close, -10, 0)]        // ちょうど＝0
    [InlineData(20, PositionEffect.Close, -10, 0)]        // 余る＝0
    [InlineData(10, PositionEffect.Close, 10, 10)]        // 同方向＝減らす対象が無い＝全量
    [InlineData(-10, PositionEffect.Close, 10, 0)]        // ショートの買い戻し（ちょうど）
    public void T16_008_賄えない数量の規則(int current, PositionEffect effect, int signed, int expected) =>
        PeriodInventory.UnvaluedQuantity(current, effect, signed).Should().Be(expected);

    // --- T-16-009: 帰属（週報 §2/§3・月報 §2 の母集合） ---

    [Fact]
    public void T16_009_帰属は算定できない決済に印を付けハイライトの母集合から外す()
    {
        var entries = FillPnlAttributionBuilder.Build(
        [
            Fill(TradeSide.Sell, PositionEffect.Close, 100, 250m, 0),               // 期間より前の建玉の決済
            Fill(TradeSide.Buy, PositionEffect.Open, 10, 200m, 60, symbol: "MSFT"),
            Fill(TradeSide.Sell, PositionEffect.Close, 10, 250m, 120, symbol: "MSFT"),
        ], Assumptions(), rationales: null);

        entries.Should().HaveCount(3);
        entries[0].Unvalued.Should().BeTrue();
        entries[0].Realizing.Should().BeFalse();
        entries[2].Unvalued.Should().BeFalse();

        // 🔴 算定できなかった決済を「損益 0 の決済」として最良・最悪に並べない。
        var highlights = FillPnlAttributionBuilder.Highlights(entries);
        highlights.Best!.Symbol.Should().Be("MSFT");
        highlights.Worst!.Symbol.Should().Be("MSFT");

        // 日別の行は件数を運ぶ（レンダラが明記するため）。
        FillPnlAttributionBuilder.ByDay(entries).Sum(r => r.UnvaluedCount).Should().Be(1);
    }

    // --- T-16-015: 方向別の内訳（幻のショートを在庫から消しても、行が残らないこと） ---

    // 🔴 素朴に「`!Realizing` なら建て」と読むと、期間より前に建てたロングの手仕舞い（Sell）が
    // **ショートの新規建て**として月報 §2 の方向別へ載る（在庫から消した幻が内訳の行に残る）。
    [Fact]
    public void T16_015_期間前の建玉の決済はロング側に数えショートの建てにしない()
    {
        var entries = FillPnlAttributionBuilder.Build(
            [Fill(TradeSide.Sell, PositionEffect.Close, 100, 250m, 0)], Assumptions(), rationales: null);

        var rows = PeriodBreakdownBuilder.ByDirection(entries);

        rows.Single(r => r.IsLong).FillCount.Should().Be(1);
        rows.Single(r => !r.IsLong).FillCount.Should().Be(0);
        // 実現損益は算定できないので、どちらの行にも積まない。
        rows.Sum(r => r.RealizedPnlGross).Should().Be(0m);
    }

    // --- T-16-010: 日報 §2 の明細 ---

    [Fact]
    public void T16_010_日報の明細は算定できない決済の実現損益を不明と描く()
    {
        var view = TradeHistoryViewBuilder.Build(
            [Fill(TradeSide.Sell, PositionEffect.Close, 100, 250m, 0)],
            Assumptions(), rationales: null, adoptions: []);

        view.Lines.Should().ContainSingle().Which.RealizedPnlUnvalued.Should().BeTrue();

        var md = TradeHistoryRenderer.RenderMarkdown(view);

        // 🔴 `0` と書かない（「損得が無かった」と読める）。§2-b と**同じ語**で「計算できない」を表す。
        md.Should().Contain("| **不明** |");
        md.Should().Contain("期間より前に建てた建玉の決済");
    }

    // --- T-16-011: §1 サマリ ---

    [Fact]
    public void T16_011_サマリは部分値になった数値を数字として出さない()
    {
        var pnl = PnlAggregator.Aggregate(
            [Fill(TradeSide.Sell, PositionEffect.Close, 100, 250m, 0)],
            Assumptions(),
            new Dictionary<string, decimal> { ["AAPL"] = 300m });

        var daily = ReportRenderer.RenderMarkdown(new ReportView
        {
            Kind = ReportKind.Daily,
            PeriodKey = "daily-2026-09-18",
            PeriodLabel = "2026-09-18",
            Markets = ["US"],
            AssumptionsVersion = 2,
            Pnl = pnl,
            BuyCount = 0,
            SellCount = 1,
            PolicySummary = "方針",
            Narrative = "散文",
        });

        daily.Should().Contain("実現損益（税引後・費用込み） | **算出不能**");
        daily.Should().Contain("評価損益（税引前・参考） | **算出不能**");
        daily.Should().Contain("源泉徴収税額 | **算出不能**");
        daily.Should().Contain("期間より前に建てた建玉の決済が 1 件");
        // 事実（取引回数）は出し続ける。
        daily.Should().Contain("取引回数（買/売/決済） | 0 / 1 / 0");

        var weekly = ReportRenderer.RenderMarkdown(new ReportView
        {
            Kind = ReportKind.Weekly,
            PeriodKey = "weekly-2026-W38",
            PeriodLabel = "2026-W38",
            Markets = ["US"],
            AssumptionsVersion = 2,
            Pnl = pnl,
            BuyCount = 0,
            SellCount = 1,
            PolicySummary = "方針",
            Narrative = "散文",
        });

        weekly.Should().Contain("勝率（勝ち取引/全決済取引） | **算出不能**");
    }

    // --- T-16-012: 為替差損益 ---

    [Fact]
    public void T16_012_為替差損益の畳み込みでも幻のショートを開かない()
    {
        // 認識時レートを持つ USD 建ての「期間より前に建てた建玉の決済」だけの期間。
        var fill = Fill(TradeSide.Sell, PositionEffect.Close, 100, 250m, 0) with
        {
            FxRateBaseToDisplay = 150m,
        };

        // 是正前は幻のショートが残り、**期末レートが要る**（期末に建玉が残る）と判定されていた。
        var result = FxTranslationBuilder.Build([fill], periodEnd: null);

        result.UnrecordedFillCount.Should().Be(0);
        // 建玉が残らないので期末レート無しでも集計でき、明細は 0 件（幻の再測定を作らない）。
        result.Summary.Should().NotBeNull();
        result.Summary!.EntryCount.Should().Be(0);
    }

    // --- T-16-013: 未供給の入力として記録される語彙 ---

    [Fact]
    public void T16_013_期間開始時点の在庫は全種別に適用される未供給の入力である()
    {
        foreach (var kind in new[] { ReportKind.Daily, ReportKind.Weekly, ReportKind.Monthly })
            ReportInputs.AppliesTo(ReportInput.OpeningInventory, kind).Should().BeTrue();

        ReportInputs.Labels([ReportInput.OpeningInventory]).Should().ContainSingle()
            .Which.Should().Be("期間開始時点の在庫");

        // 永続化形式の往復（列挙名。読み出し側の互換のため名前を固定する）。
        ReportInputs.Parse(ReportInputs.Serialize([ReportInput.OpeningInventory]))
            .Should().Equal(ReportInput.OpeningInventory);
    }

    // --- T-16-014: 現在値の解決 ---

    [Fact]
    public async Task T16_014_幻のショートの銘柄の相場を取りに行かない()
    {
        var marketData = new RecordingMarketDataSource();
        var service = new ReportDraftService(new StubDrafter(), marketData);

        await service.BuildDraftAsync(new DraftRequest(
            ReportKind.Daily, "daily-2026-09-18", new DateOnly(2026, 9, 18), ["US"], 2, null, "方針",
            [Fill(TradeSide.Sell, PositionEffect.Close, 100, 250m, 0)],
            CurrentPrices: null));

        // 期間の在庫は残らない（幻のショートを開かない）ため、相場照会そのものが起きない。
        marketData.RequestedSymbols.Should().BeEmpty();
    }

    private sealed class RecordingMarketDataSource : AiStockTrading.Shared.Contracts.Ports.IMarketDataSource
    {
        public List<string> RequestedSymbols { get; } = [];

        public Task<Quote?> GetLatestQuoteAsync(
            string symbol, Market market, CancellationToken cancellationToken = default)
        {
            RequestedSymbols.Add(symbol);
            return Task.FromResult<Quote?>(null);
        }
    }

    private sealed class StubDrafter : IReportNarrativeDrafter
    {
        public Task<string> DraftNarrativeAsync(
            ReportNarrativeContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("散文");
    }
}
