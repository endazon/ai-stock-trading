using ReportService.Features.Reports;
using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-11, FR-16, ADR-0041 決定 1, #870, #859, IADR-0360 決定 4（2026-09-19 改定）:
// 🔴 **取り込みは在庫を「減らす」ことしかできない。建てない・反転しない。**
//
// 報告書側の在庫は**その期間の約定だけ**から畳まれる（PeriodFillQuery が期間で切る）。したがって
// **期間より前に建てた建玉は、報告書の在庫に存在しない。** それを手動で売った取り込みを素の
// SignedInventory.Apply へ通すと、「在庫 0 に売りを適用」＝**平均取得単価 0 の幻のショート**が開く
// （SignedInventory は在庫 0 を新規建てとして扱い、反転では余りを新しい建玉にする）。
//
// 幻のショートは
//   - ResolveCurrentPricesAsync が現在値を引く対象になり、**評価損益として日報 §1 に出る**
//   - 後続のシステムの売り約定を「決済」ではなく「新規ショート」に変え、**実現損益・決済件数・勝ち決済件数を汚す**
// ——いずれも #859 が止めようとした「実在しない建玉の評価損益」を**符号違いで再導入**する。
//
// 本ファイルはその 4 経路を固定する（監査 BLK-1 の再現）。
public class DriftAdoptionPhantomPositionTests
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

    private static PeriodTradeFill Buy(int qty, decimal price, int minute) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, qty, price, T0.AddMinutes(minute));

    private static PeriodTradeFill Sell(int qty, decimal price, int minute) =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, qty, price, T0.AddMinutes(minute));

    /// <summary>台帳の建玉を <paramref name="before"/> → <paramref name="after"/> へ合わせる取り込み（ロングの減少＝売り）。</summary>
    private static PeriodDriftAdoption Adoption(int before, int after, int minute) =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Sell, before - after, before, after,
            T0.AddMinutes(minute - 1), "owner", "moomoo アプリから直接売却したため。", T0.AddMinutes(minute));

    private static readonly IReadOnlyDictionary<string, decimal> Prices =
        new Dictionary<string, decimal> { ["AAPL"] = 300m };

    // ---- A: 最も普通のケース（昨日買った建玉を今日アプリで売り、今日取り込む） ----

    [Fact]
    public void 期間前に建てた建玉の取り込みは_幻のショートを作らない()
    {
        // 当期の約定は 1 件も無い。台帳は 100 株持っていたが、それは**前期の買い**である
        //（報告書の在庫には存在しない）。
        var summary = PnlAggregator.Aggregate([], Assumptions(), Prices, [Adoption(before: 100, after: 0, minute: 5)]);

        // 🔴 減らす在庫が無いなら**何も起きない**。平均取得単価 0 のショートを開かない。
        summary.UnrealizedPnl.Should().Be(0m);
        summary.RealizedPnlGross.Should().Be(0m);
        summary.RealizingTradeCount.Should().Be(0);
        summary.TradeCount.Should().Be(0);
    }

    // ---- B: 取り込みが当期の在庫を超える（台帳＝前期 100 ＋ 当期 10） ----

    [Fact]
    public void 当期の在庫を超える取り込みは_超過分で幻のショートを作らない()
    {
        PeriodTradeFill[] fills = [Buy(10, 200m, 0)];

        var summary = PnlAggregator.Aggregate(
            fills, Assumptions(), Prices, [Adoption(before: 110, after: 0, minute: 5)]);

        // 当期の 10 株は消える。**超過した 100 株ぶんはショートにならない**（報告書は前期の在庫を知らない）。
        summary.UnrealizedPnl.Should().Be(0m);
        summary.RealizedPnlGross.Should().Be(0m);
    }

    // ---- C: 幻のショートが後続の約定の実現損益・決済件数・勝率を汚す ----

    [Fact]
    public void 期間前建玉の取り込みの後の売り約定が_実現損益と決済件数を汚さない()
    {
        // 取り込み（前期建玉 10 株）の後に、当期のシステムが 10 株建てて 10 株決済する。
        PeriodTradeFill[] fills = [Buy(10, 200m, 10), Sell(10, 400m, 20)];

        var summary = PnlAggregator.Aggregate(
            fills, Assumptions(), Prices, [Adoption(before: 10, after: 0, minute: 5)]);

        // (400 − 200) × 10 ＝ 2,000 の**利益決済 1 件**。取り込みがこれを 1 件も動かしてはならない。
        summary.RealizedPnlGross.Should().Be(2_000m);
        summary.RealizingTradeCount.Should().Be(1);
        summary.WinningTradeCount.Should().Be(1);
        summary.UnrealizedPnl.Should().Be(0m);
    }

    [Fact]
    public void 帰属行も同じ畳み込みで汚れない()
    {
        PeriodTradeFill[] fills = [Buy(10, 200m, 10), Sell(10, 400m, 20)];
        PeriodDriftAdoption[] adoptions = [Adoption(before: 10, after: 0, minute: 5)];

        var attributions = FillPnlAttributionBuilder.Build(fills, Assumptions(), null, adoptions);
        var history = TradeHistoryViewBuilder.Build(fills, Assumptions(), null, adoptions);

        attributions.Single(a => a.Realizing).RealizedPnlGross.Should().Be(2_000m);
        history.Lines.Last().RealizedPnl.Should().Be(2_000m);

        // 🔴 内訳の合計は §1 サマリと一致し続ける（畳み込み規則が 3 経路で同じであることの担保）。
        var summary = PnlAggregator.Aggregate(fills, Assumptions(), null, adoptions);
        attributions.Sum(a => a.RealizedPnlGross).Should().Be(summary.RealizedPnlGross);
    }

    // ---- D: 日報の生成経路（ReportDraftService）でも出ない ----

    [Fact]
    public async Task 日報の生成経路でも幻の建玉の評価損益が出ない()
    {
        // 🔴 ResolveCurrentPricesAsync が幻のショートの現在値を**市場データ源まで引きに行かない**ことも見る。
        var market = new StubMarketData(new() { [("AAPL", Market.UnitedStates)] = 300m });
        var sut = new ReportDraftService(new FakeDrafter(), market);

        var draft = await sut.BuildDraftAsync(new DraftRequest(
            ReportKind.Daily, "daily-2026-09-18", new DateOnly(2026, 9, 18), ["US"], 1, null, "方針",
            [Buy(10, 200m, 0)], CurrentPrices: null,
            DriftAdoptions: [Adoption(before: 110, after: 0, minute: 5)]));

        draft.Pnl.UnrealizedPnl.Should().Be(0m);
        market.Requested.Should().BeEmpty("建玉が残らないので現在値を引く必要が無い");
    }

    // ---- 為替差損益（既に専用の防御を持つ経路）も同じ規則であること ----

    [Fact]
    public void 為替差損益も_在庫を超える取り込みで幻の建玉を作らない()
    {
        PeriodTradeFill[] fills = [Buy(10, 200m, 0) with { FxRateBaseToDisplay = 150m }];
        var periodEnd = new PeriodEndFxRate(160m, new DateOnly(2026, 9, 18));

        var result = FxTranslationBuilder.Build(fills, periodEnd, [Adoption(before: 110, after: 0, minute: 5)]);

        result.Summary.Should().NotBeNull();
        result.Summary!.TranslationGainJpy.Should().Be(0m);
        result.Summary.EntryCount.Should().Be(0);
    }

    // ---- 規則そのもの（純関数） ----

    [Theory]
    // 在庫が無い → 効かない（幻の建玉を開かない）
    [InlineData(0, -100, 0)]
    // 同方向（あり得ないが念のため） → 効かない
    [InlineData(10, 10, 10)]
    // 通常の一部減少
    [InlineData(10, -6, 4)]
    // ちょうど 0
    [InlineData(10, -10, 0)]
    // 在庫を超える → 0 でクランプ（反転しない）
    [InlineData(10, -110, 0)]
    // ショート建玉の減少（方向が逆）
    [InlineData(-50, 30, -20)]
    // ショート建玉を超える減少 → 0 でクランプ
    [InlineData(-50, 80, 0)]
    public void 取り込みは減らす方向にしか効かない(int current, int signed, int expected)
    {
        PeriodDriftAdoption.ReducedQuantity(current, signed).Should().Be(expected);
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
                ? new Quote(symbol, market, price, T0)
                : null);
        }
    }
}
