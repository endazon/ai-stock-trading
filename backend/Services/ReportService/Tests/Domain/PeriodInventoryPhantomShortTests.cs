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
// テスト ID は **T-16-001〜023**（本作業で新設した帯。T-16-016〜023 は監査の指摘で足した。走査の結果、本リポジトリに `T-16` 帯は
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

    // --- T-16-016: 散文プロンプト（LLM へ部分値を権威として渡さない） ---

    // 🔴 本文・要約は「算出不能」と描くのに、プロンプトだけが部分値を「コードで確定済みの集計値」として渡すと、
    // 散文が「当期は決済が無かった」「損益 0 だった」と書ける（数字を黙って出すより悪い）。
    private static PnlSummary PartialPnl(int unvalued) => new(
        RealizedPnlGross: 777m, TotalCost: 56m, TaxWithheld: 131m, RealizedPnlNet: 590m,
        UnrealizedPnl: -4321m, TradeCount: 5, RealizingTradeCount: 3, WinningTradeCount: 2,
        UnvaluedSettlementCount: unvalued);

    private static ReportNarrativeContext NarrativeContext(int unvalued) => new(
        ReportKind.Daily, "daily-2026-09-18", "2026-09-18", ["US"], PartialPnl(unvalued), "方針");

    [Fact]
    public void T16_016_散文プロンプトは部分値を渡さず言及しないよう指示する()
    {
        var prompt = ReportNarrativePromptBuilder.Build(NarrativeContext(unvalued: 1));

        prompt.Should().Contain("- 実現損益(税引前): 算出不能（1件）");
        prompt.Should().Contain("- 源泉徴収税額: 算出不能（1件）");
        prompt.Should().Contain("- 実現損益(税引後): 算出不能（1件）");
        prompt.Should().Contain("- 評価損益(参考): 算出不能（1件）");
        prompt.Should().Contain("- 約定件数: 5 / 決済件数: 算出不能（1件） / 勝ち決済: 算出不能（1件）");
        // 部分値そのものがプロンプトのどこにも現れない。
        foreach (var partial in new[] { "777", "590", "131", "4321", "決済件数: 3", "勝ち決済: 2" })
            prompt.Should().NotContain(partial);
        // 言及しない指示（「決済が無かった」「損益 0」の否定を含む）。
        prompt.Should().Contain("散文で一切言及しないでください");
        prompt.Should().Contain("「決済が無かった」「損益は 0 だった」");
        // 取得原価を要さない費用・約定件数はそのまま渡す。
        prompt.Should().Contain("- 費用合計: 56");

        // 対の肯定形: 算定できない決済が無ければ従来どおり数値を渡し、指示も付けない。
        var normal = ReportNarrativePromptBuilder.Build(NarrativeContext(unvalued: 0));
        normal.Should().Contain("- 実現損益(税引前): 777");
        normal.Should().Contain("決済件数: 3 / 勝ち決済: 2");
        normal.Should().NotContain("算出不能");
        normal.Should().NotContain("一切言及しないでください");
    }

    // --- T-16-017: 週報 §5 の源泉徴収税額 ---

    private static ReportView WeeklyWithCostReview(PnlSummary pnl, IReadOnlyList<FillPnlAttribution>? entries = null) => new()
    {
        Kind = ReportKind.Weekly,
        PeriodKey = "weekly-2026-W38",
        PeriodLabel = "2026-W38",
        Markets = ["US"],
        AssumptionsVersion = 2,
        Pnl = pnl,
        PolicySummary = "方針",
        Narrative = "散文",
        FillAttributions = entries,
        CostReview = new PeriodCostReview(
            Commission: 40m, FxSpread: 16m, TotalCost: 56m, TaxWithheld: 131m, RealizedPnlGross: 777m, CostRatio: 0.0721m),
    };

    private static string RiskCostSection(string md)
    {
        var start = md.IndexOf("## 5. リスク・費用レビュー", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = md.IndexOf("## 6.", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return md[start..end];
    }

    [Fact]
    public void T16_017_週報の費用レビューは部分値の源泉徴収税額を数字として出さない()
    {
        var section = RiskCostSection(ReportRenderer.RenderMarkdown(WeeklyWithCostReview(PartialPnl(1))));

        section.Should().Contain("| 源泉徴収税額 | **算出不能**（期間より前に建てた建玉の決済が 1 件あり");
        section.Should().NotContain("131");
        // 費用は約定ごとに掛かり取得原価を要さない（出し続ける）。
        section.Should().Contain("| 費用合計（§1 と同じ値） | +56.00 USD |");

        // 対の肯定形: 算定できない決済が無ければ税額を出す。
        RiskCostSection(ReportRenderer.RenderMarkdown(WeeklyWithCostReview(PartialPnl(0))))
            .Should().Contain("| 源泉徴収税額 | +131.00 USD |");
    }

    // --- T-16-018: 週報 §5 の費用率 ---

    [Fact]
    public void T16_018_週報の費用率は分母が部分値なら算出不能と描く()
    {
        var section = RiskCostSection(ReportRenderer.RenderMarkdown(WeeklyWithCostReview(PartialPnl(1))));

        section.Should().Contain("- 損益に対する費用率: **算出不能**（期間より前に建てた建玉の決済が 1 件あり");
        // 部分値の分母から計算した比率（7.2%）を出さない。
        section.Should().NotContain("7.2%");
        section.Should().NotContain("+777.00 USD");

        RiskCostSection(ReportRenderer.RenderMarkdown(WeeklyWithCostReview(PartialPnl(0))))
            .Should().Contain("- 損益に対する費用率: 7.2%");
    }

    // --- T-16-019: 期間開始時点の在庫が未供給として**記録される**（結線） ---

    // T-16-013 は語彙だけを見る。🔴 **記録する側（自動生成）が 1 行消えても T-16-013 は緑のまま**なので、
    // 保存された報告書の未供給入力を見る。
    [Fact]
    public async Task T16_019_期間前の建玉の決済を検出したら期間開始時点の在庫を未供給として記録する()
    {
        // 2026-07-08（水）16:00 JST ＝ 07:00 UTC。日報だけが生成境界を越えている時刻。
        var now = new DateTimeOffset(2026, 7, 8, 7, 0, 0, TimeSpan.Zero);
        var at = new DateTimeOffset(2026, 7, 8, 14, 30, 0, TimeSpan.Zero);

        async Task<IReadOnlyList<ReportInput>> UnsuppliedFor(params PeriodTradeFill[] fills)
        {
            var store = new ReportService.Infrastructure.Persistence.InMemoryReportStore();
            await new ReportAutoGenerator(
                store,
                new ReportDraftService(new StubDrafter()),
                new StubFillSource(fills),
                new FixedClock(now),
                new ReportAutoGenerationSettings()).RunOnceAsync();
            return store.List().Single(r => r.Kind == ReportKind.Daily).UnsuppliedInputs;
        }

        (await UnsuppliedFor(new PeriodTradeFill(
                "AAPL", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 100, 250m, at)))
            .Should().Contain(ReportInput.OpeningInventory);

        // 対の否定形: 期間内で建てて決済しただけなら記録しない（常に立つ警告は警告にならない）。
        (await UnsuppliedFor(
                new PeriodTradeFill("AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 200m, at),
                new PeriodTradeFill(
                    "AAPL", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 10, 250m, at.AddMinutes(30))))
            .Should().NotContain(ReportInput.OpeningInventory);
    }

    // --- T-16-020: Discord 要約 ---

    [Fact]
    public void T16_020_通知の要約は部分値の実現損益と決済件数を数字として出さない()
    {
        var summary = ReportSummary.Build(ReportKind.Daily, "2026-09-18", PartialPnl(1), "所感");

        summary.Should().Contain("実現損益（税引後・費用込み）: 算出不能（期間より前に建てた建玉の決済 1 件）");
        summary.Should().Contain("取引: 5 件（決済・勝ちは算出不能）");
        summary.Should().NotContain("590");
        summary.Should().NotContain("決済 3");
        summary.Should().NotContain("勝ち 2");
        // 費用は出し続ける。
        summary.Should().Contain("費用: +56.00 USD");

        var normal = ReportSummary.Build(ReportKind.Daily, "2026-09-18", PartialPnl(0), "所感");
        normal.Should().Contain("取引: 5 件（決済 3・勝ち 2）");
        normal.Should().NotContain("算出不能");
    }

    // --- T-16-021: ハイライト・主な要因（一部だけ賄えた決済） ---

    // 🔴 T-16-009 は「全量を賄えない決済」（部分値 0）だけを見る。**一部だけ賄えた決済は `Realizing` かつ
    // `Unvalued`** であり、部分値（多くは 0 でない）で最良・最悪・寄与最大に並び得る。
    [Fact]
    public void T16_021_一部だけ賄えた決済は部分値でハイライトと寄与最大に並ばない()
    {
        var entries = FillPnlAttributionBuilder.Build(
        [
            Fill(TradeSide.Buy, PositionEffect.Open, 5, 200m, 0),                      // 当期に 5 株建てる
            Fill(TradeSide.Sell, PositionEffect.Close, 10, 250m, 60),                  // 10 株を決済（5 株は期間より前の建玉）
            Fill(TradeSide.Buy, PositionEffect.Open, 10, 200m, 120, symbol: "MSFT"),
            Fill(TradeSide.Sell, PositionEffect.Close, 10, 210m, 180, symbol: "MSFT"), // +100（全量を算定）
        ], Assumptions(), rationales: null);

        // 前提: 一部だけ賄えた決済は「決済」かつ「算定できない」であり、部分値（+250）は MSFT（+100）より大きい。
        entries[1].Realizing.Should().BeTrue();
        entries[1].Unvalued.Should().BeTrue();
        entries[1].RealizedPnlGross.Should().Be(250m);

        var highlights = FillPnlAttributionBuilder.Highlights(entries);
        highlights.Best!.Symbol.Should().Be("MSFT");
        highlights.Worst!.Symbol.Should().Be("MSFT");

        var day = FillPnlAttributionBuilder.ByDay(entries).Should().ContainSingle().Subject;
        day.LargestContributor!.Symbol.Should().Be("MSFT");
        day.UnvaluedCount.Should().Be(1);
    }

    // --- T-16-022: 三者比較の算入できなかった件数 ---

    [Fact]
    public void T16_022_三者比較は到達済みの列ごとに算入できなかった決済を合算して明記する()
    {
        PeriodTradeFill PreviousClose(BrokerProvider provider, int minutes) =>
            new("AAPL", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 10, 120m,
                T0.AddMinutes(minutes), Guid.NewGuid(), provider);

        IReadOnlyList<PeriodTradeFill> fills =
        [
            PreviousClose(BrokerProvider.MoomooSimulate, 0),
            PreviousClose(BrokerProvider.MoomooSimulate, 10),
            PreviousClose(ThreeWayComparisonAggregator.LiveProvider, 20),
        ];

        // 両列に到達: SIMULATE 2 件 ＋ 実弾 1 件。
        var live = ThreeWayComparisonAggregator.Aggregate(fills, Assumptions(), TradingStage.Stage2MinimalLive);
        live!.UnvaluedSettlementCount.Should().Be(3);

        // 実弾列は未到達（空欄）: 空欄の列の分は数えない。
        ThreeWayComparisonAggregator.Aggregate(fills, Assumptions(), TradingStage.Stage1Simulate)!
            .UnvaluedSettlementCount.Should().Be(2);

        // 出口（月報 §5）に件数が出る。
        var md = ReportRenderer.RenderMarkdown(new ReportView
        {
            Kind = ReportKind.Monthly,
            PeriodKey = "monthly-2026-09",
            PeriodLabel = "2026-09",
            Markets = ["US"],
            AssumptionsVersion = 2,
            Pnl = PartialPnl(3),
            PolicySummary = "方針",
            Narrative = "散文",
            ThreeWayComparison = live,
        });
        md.Should().Contain("- 期間より前に建てた建玉の決済が 3 件あり、**勝率・平均損益に算入していません**");
    }

    // --- T-16-023: 内訳の節の注記 ---

    private const string SectionNote = "- **期間より前に建てた建玉の決済が 1 件あり、実現損益・勝率へ算入していません**";

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public void T16_023_内訳の各節は算入しなかった決済の件数を注記する()
    {
        var onlyPrevious = FillPnlAttributionBuilder.Build(
            [Fill(TradeSide.Sell, PositionEffect.Close, 100, 250m, 0)], Assumptions(), rationales: null);
        var withRoundTrip = FillPnlAttributionBuilder.Build(
        [
            Fill(TradeSide.Sell, PositionEffect.Close, 100, 250m, 0),
            Fill(TradeSide.Buy, PositionEffect.Open, 10, 200m, 60, symbol: "MSFT"),
            Fill(TradeSide.Sell, PositionEffect.Close, 10, 210m, 120, symbol: "MSFT"),
        ], Assumptions(), rationales: null);

        // 週報: §2 日別推移と §3 ハイライト（決済の無い経路・有る経路の両方）にそれぞれ 1 回ずつ。
        foreach (var entries in new[] { onlyPrevious, withRoundTrip })
        {
            var weekly = ReportRenderer.RenderMarkdown(WeeklyWithCostReview(PartialPnl(1), entries));
            Occurrences(weekly, SectionNote).Should().Be(2);
            weekly.IndexOf("## 3. ハイライト取引", StringComparison.Ordinal)
                .Should().BeLessThan(weekly.LastIndexOf(SectionNote, StringComparison.Ordinal));
        }

        // 月報: §2 内訳に 1 回。
        var monthly = ReportRenderer.RenderMarkdown(new ReportView
        {
            Kind = ReportKind.Monthly,
            PeriodKey = "monthly-2026-09",
            PeriodLabel = "2026-09",
            Markets = ["US"],
            AssumptionsVersion = 2,
            Pnl = PartialPnl(1),
            PolicySummary = "方針",
            Narrative = "散文",
            FillAttributions = withRoundTrip,
        });
        Occurrences(monthly, SectionNote).Should().Be(1);

        // 対の否定形: 算定できない決済が無ければ注記しない。
        var clean = FillPnlAttributionBuilder.Build(
        [
            Fill(TradeSide.Buy, PositionEffect.Open, 10, 200m, 60, symbol: "MSFT"),
            Fill(TradeSide.Sell, PositionEffect.Close, 10, 210m, 120, symbol: "MSFT"),
        ], Assumptions(), rationales: null);
        ReportRenderer.RenderMarkdown(WeeklyWithCostReview(PartialPnl(0), clean))
            .Should().NotContain("期間より前に建てた建玉の決済");
    }

    private sealed class FixedClock(DateTimeOffset now) : ReportService.Common.Abstractions.IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class StubFillSource(IReadOnlyList<PeriodTradeFill> fills) : IPeriodFillSource
    {
        public Task<IReadOnlyList<PeriodTradeFill>> GetFillsAsync(
            DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult(fills);
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
