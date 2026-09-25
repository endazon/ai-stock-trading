using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Infrastructure.ExternalServices;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10 (1)(3)(6), UC-06, ADR-0016 決定2(a)・決定3・決定9, #967, IADR-0425（T-10-1020〜T-10-1024）:
// 空売り文脈の供給元（ShortSellContextSupplier）とエクスポージャの射影（ShortExposureProjection）と、審査がその文脈を判定コアへ渡すこと。
//
// 🔴 本節が守るのは 2 つの向きである。
//   1. **分からないものを 0 や false で埋めない**（借株可否が分からない・保有建玉の現在値が無い → 文脈を組まない＝拒否）。
//   2. **未約定の空売りを数える**（数えないと、指値が溜まっている間に上限を超えて承認し続ける。#829 と同型の穴）。
public class ShortSellContextSupplierTests
{
    // 米国東部の取引時間中（2026-07-09 木 10:00 ET）。未約定の新規建ては承認時刻の現地取引日が当日のものだけ数える。
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 7, 9);

    private static OrderIntent ShortEntry(string symbol = "MSFT", int quantity = 10, decimal price = 40m) =>
        new(symbol, Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell, BrokerProvider.InternalPaper,
            quantity, price, PositionEffect.Open, StopLossPrice: price * 1.1m);

    private static OrderIntent Entry(string symbol, TradeSide side, int quantity, decimal price) =>
        new(symbol, Market.UnitedStates, side,
            side == TradeSide.Sell ? ProductType.ShortSell : ProductType.Cash, BrokerProvider.InternalPaper,
            quantity, price, PositionEffect.Open, StopLossPrice: side == TradeSide.Sell ? price * 1.1m : price * 0.9m);

    private static WorkingEntryOrder Working(string symbol, TradeSide side, int quantity, decimal price, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), symbol, Market.UnitedStates, side, quantity, price, Now.AddMinutes(-5));

    private static IReadOnlyDictionary<(string Symbol, Market Market), decimal> Prices(params (string Symbol, decimal Price)[] prices) =>
        prices.ToDictionary(p => (p.Symbol, Market.UnitedStates), p => p.Price);

    // ------------------------------------------------------------------
    // T-10-1020 / T-10-1021: エクスポージャの射影
    // ------------------------------------------------------------------

    /// <summary>T-10-1020: 保有は時価、未約定は残数量 × 承認価格。売り建ては空売り、買い建てはロングとして建玉総額へ入る。</summary>
    [Fact]
    public void エクスポージャは保有を時価で未約定を残数量と承認価格で数える()
    {
        var held = new[]
        {
            new OpenPosition("AAPL", Market.UnitedStates, TradeSide.Buy, 20, 90m),   // 取得 90・時価 100 → 2,000
            new OpenPosition("MSFT", Market.UnitedStates, TradeSide.Sell, 5, 30m),   // 取得 30・時価 40 → 200（取得原価なら 150）
        };
        var working = new (WorkingEntryOrder, int)[]
        {
            (Working("MSFT", TradeSide.Sell, 10, 41m), 4),   // 未約定の空売り 4 株 × 41 = 164
            (Working("TSLA", TradeSide.Sell, 3, 50m), 3),    // 別銘柄の未約定の空売り 150
            (Working("NVDA", TradeSide.Buy, 2, 100m), 2),    // 未約定の買い建て 200（ロング）
        };

        var exposure = ShortExposureProjection.Project(
            "MSFT", Market.UnitedStates, held, Prices(("AAPL", 100m), ("MSFT", 40m)), working);

        exposure.Should().Be(new ShortExposure(
            SymbolShortExposure: 200m + 164m,
            TotalShortExposure: 200m + 164m + 150m,
            TotalExposure: 2_000m + 200m + 164m + 150m + 200m));
    }

    /// <summary>T-10-1020: 約定済みの分は保有へ、残りだけが未約定へ入る（同じ注文を二重に数えない）。</summary>
    [Fact]
    public async Task 一部約定の空売りは約定分を保有に残数量を未約定に数え二重に数えない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var id = Guid.NewGuid();
        var intent = Entry("MSFT", TradeSide.Sell, 10, 40m);
        ledger.AppendApproval(id, intent, Now.AddMinutes(-5));
        ledger.AppendFill(id, "ORD-1", 6, 40m, Now.AddMinutes(-4)); // 10 株中 6 株約定
        var supplier = TestShortSellContexts.Supplier(
            new FixedShortSellBorrowSource(ShortSellBorrowObservation.Permitted()), new FakeClock(Now, Today), ledger,
            new FixedWorkingEntries([Working("MSFT", TradeSide.Sell, 10, 40m, id)]),
            new FixedCurrentPrices(Prices(("MSFT", 40m))));

        var context = await supplier.SupplyAsync(ShortEntry(), Today, null, TestContext.Current.CancellationToken);

        // 保有 6 × 40 = 240、未約定 (10 − 6) × 40 = 160。承認数量 10 × 40 を未約定にも数えると 640 になる。
        context!.SymbolShortExposure.Should().Be(400m);
        context.TotalShortExposure.Should().Be(400m);
        context.TotalExposure.Should().Be(400m);
    }

    /// <summary>T-10-1021: 保有建玉の現在値が 1 件でも無ければ「分からない」（0 と数えない）。</summary>
    [Fact]
    public void 保有建玉の現在値が欠ければエクスポージャは分からない()
    {
        var held = new[]
        {
            new OpenPosition("AAPL", Market.UnitedStates, TradeSide.Buy, 20, 90m),
            new OpenPosition("MSFT", Market.UnitedStates, TradeSide.Sell, 5, 30m),
        };

        ShortExposureProjection.Project("MSFT", Market.UnitedStates, held, Prices(("AAPL", 100m)), [])
            .Should().BeNull("空売りの建玉の現在値が無いまま 0 と数えると、1 銘柄 10% も空売り比率 50% も緩む");
        ShortExposureProjection.Project("MSFT", Market.UnitedStates, held, Prices(("MSFT", 40m)), [])
            .Should().BeNull("ロングの現在値が無いまま 0 と数えると、空売り比率の分母が縮む（こちらは厳しい側だが、観測していない値である）");
    }

    /// <summary>T-10-1021: 建玉も未約定も無ければ 0（無いことを台帳で確かめた値）。「分からない」とは別の状態である。</summary>
    [Fact]
    public void 建玉も未約定も無ければエクスポージャは0として分かる()
    {
        ShortExposureProjection.Project("MSFT", Market.UnitedStates, [], Prices(), [])
            .Should().Be(new ShortExposure(0m, 0m, 0m));
    }

    // ------------------------------------------------------------------
    // T-10-1022 / T-10-1023: 文脈の組み立て
    // ------------------------------------------------------------------

    /// <summary>T-10-1022: 借株可否が分かれば組む。料率・権利確定日は供給元が無いので null、禁止期限は載せる。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 借株可否が分かれば文脈を組み料率と権利確定日は載せない(bool permitted)
    {
        var banUntil = new DateOnly(2026, 8, 1);
        var supplier = TestShortSellContexts.Supplier(
            new FixedShortSellBorrowSource(permitted
                ? ShortSellBorrowObservation.Permitted()
                : ShortSellBorrowObservation.NotPermitted()),
            new FakeClock(Now, Today));

        var context = await supplier.SupplyAsync(ShortEntry(), Today, banUntil, TestContext.Current.CancellationToken);

        context.Should().NotBeNull();
        context!.ShortPermit.Should().Be(permitted);
        context.Today.Should().Be(Today);
        context.BuyInBanUntil.Should().Be(banUntil);
        // 🔴 料率は単位未確定のため写像しない（null ＝ BorrowUnavailable が立ち続ける）。権利確定日は供給元が無い。
        // 料率の供給を始めるなら、先に権利確定日の「不明」を拒否へ倒す手当てが要る（IADR-0425 の残余リスク）。
        context.BorrowRateAnnual.Should().BeNull();
        context.DividendRecordDate.Should().BeNull();
        context.MarginSnapshot.Should().BeNull("既定の維持率の供給は「供給なし」");
        context.SymbolShortExposure.Should().Be(0m);
        context.TotalShortExposure.Should().Be(0m);
        context.TotalExposure.Should().Be(0m);
    }

    /// <summary>T-10-1023: 借株可否が分からなければ組まない（照会できないなら空売りしない）。</summary>
    [Fact]
    public async Task 借株可否が分からなければ文脈を組まない()
    {
        var supplier = TestShortSellContexts.Unavailable(new FakeClock(Now, Today));

        (await supplier.SupplyAsync(ShortEntry(), Today, null, TestContext.Current.CancellationToken))
            .Should().BeNull();
    }

    /// <summary>T-10-1023: エクスポージャが分からなければ（保有建玉の現在値が無い）借株が許可されていても組まない。</summary>
    [Fact]
    public async Task エクスポージャが分からなければ借株が許可されていても文脈を組まない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var id = Guid.NewGuid();
        ledger.AppendApproval(id, Entry("AAPL", TradeSide.Buy, 20, 100m), Now.AddHours(-30));
        ledger.AppendFill(id, "ORD-L", 20, 100m, Now.AddHours(-30));
        var supplier = TestShortSellContexts.Supplier(
            new FixedShortSellBorrowSource(ShortSellBorrowObservation.Permitted()), new FakeClock(Now, Today), ledger);

        (await supplier.SupplyAsync(ShortEntry(), Today, null, TestContext.Current.CancellationToken))
            .Should().BeNull("保有建玉の現在値が無いのにエクスポージャ 0 で組むと、観測していない値の上で 10% / 50% を判定する");
    }

    // ------------------------------------------------------------------
    // T-10-1023 / T-10-1024: 審査
    // ------------------------------------------------------------------

    private static (OrderScreeningService Service, FixedShortSellBorrowSource Borrow) Screening(
        ShortSellBorrowObservation borrow, InMemoryPortfolioLedgerStore? ledger = null,
        IReadOnlyDictionary<(string Symbol, Market Market), decimal>? prices = null)
    {
        var clock = new FakeClock(Now, Today);
        ledger ??= new InMemoryPortfolioLedgerStore();
        var settings = TradingDefaults.CreateSettings();
        var enabled = new HashSet<ProductType> { ProductType.Cash, ProductType.MarginLong, ProductType.ShortSell };
        var builder = new PortfolioSnapshotBuilder(
            new FakePortfolioStateProvider(new PortfolioState { LedgerEquity = 3_000m }),
            new InMemoryKillSwitchStore(), new InMemoryPauseStore(),
            FakeBrokerAccountObservations.Margin(), FakeInformationDegradation.Affirmed(),
            capitalBaseline: FakeCapitalBaseline.Of(3_000m));
        var source = new FixedShortSellBorrowSource(borrow);
        var service = new OrderScreeningService(
            new InMemoryRiskSettingsStore(settings with { Guard = settings.Guard with { EnabledProductTypes = enabled } }),
            builder, new InMemoryLockoutStore(), clock, new WeekendBusinessCalendar(),
            new InMemoryBuyInInferenceStore(), ledger,
            TestShortSellContexts.Supplier(source, clock, ledger, prices: new FixedCurrentPrices(prices ?? Prices())));
        return (service, source);
    }

    private static InMemoryPortfolioLedgerStore LongHeld(string symbol, int quantity, decimal price)
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var id = Guid.NewGuid();
        ledger.AppendApproval(id, Entry(symbol, TradeSide.Buy, quantity, price), Now.AddHours(-30));
        ledger.AppendFill(id, "ORD-" + symbol, quantity, price, Now.AddHours(-30));
        return ledger;
    }

    /// <summary>
    /// T-10-1024: 審査は組んだ文脈を判定コアへ渡し、1 銘柄 10%（基準資金 3,000 の 300）を超える空売りは ShortExposureExceeded で拒否される。
    /// 料率が供給されない（単位未確定）ため BorrowUnavailable も立つ——全件拒否は続き、上限は監査に載る。
    /// </summary>
    [Fact]
    public async Task 審査は文脈を判定コアへ渡し1銘柄10パーセント超の空売りは上限で拒否される()
    {
        // ロング AAPL 20 × 100 = 2,000（比率 50% の分母を満たす）。空売り MSFT 10 × 40 = 400 > 300（10%）。
        var (service, _) = Screening(ShortSellBorrowObservation.Permitted(), LongHeld("AAPL", 20, 100m), Prices(("AAPL", 100m)));

        var outcome = await service.ScreenAsync(
            new TradeDecisionMade(Guid.NewGuid(), ShortEntry(quantity: 10, price: 40m), "テスト判断", Now),
            TestContext.Current.CancellationToken);

        outcome.Rejected!.Reasons.Should().Contain(RejectionReason.ShortExposureExceeded)
            .And.Contain(RejectionReason.BorrowUnavailable);
    }

    /// <summary>T-10-1024: ロングが無ければ空売り比率 50% を超え ShortExposureExceeded（1 銘柄 10% の内側の数量でも）。</summary>
    [Fact]
    public async Task 審査はロングが無い空売りを空売り比率50パーセントで拒否する()
    {
        // 空売り MSFT 10 × 25 = 250 ≦ 300（10% の内側）。ロング 0 → 250 > (0 + 250) × 0.5。
        var (service, _) = Screening(ShortSellBorrowObservation.Permitted());

        var outcome = await service.ScreenAsync(
            new TradeDecisionMade(Guid.NewGuid(), ShortEntry(quantity: 10, price: 25m), "テスト判断", Now),
            TestContext.Current.CancellationToken);

        outcome.Rejected!.Reasons.Should().Contain(RejectionReason.ShortExposureExceeded);
    }

    /// <summary>T-10-1024 の対照: 上限の内側なら ShortExposureExceeded は立たない（上限の理由が文脈の値に由来することの確認）。</summary>
    [Fact]
    public async Task 上限の内側の空売りには上限の理由が立たない()
    {
        // ロング 2,000・空売り MSFT 10 × 25 = 250（≦ 300・250 ≦ 2,250 × 0.5）。
        var (service, _) = Screening(ShortSellBorrowObservation.Permitted(), LongHeld("AAPL", 20, 100m), Prices(("AAPL", 100m)));

        var outcome = await service.ScreenAsync(
            new TradeDecisionMade(Guid.NewGuid(), ShortEntry(quantity: 10, price: 25m), "テスト判断", Now),
            TestContext.Current.CancellationToken);

        outcome.Rejected!.Reasons.Should().NotContain(RejectionReason.ShortExposureExceeded)
            .And.Contain(RejectionReason.BorrowUnavailable, "料率が供給されない間は、上限の内側でも空売りは通らない");
    }

    /// <summary>T-10-1024: 未約定の空売りが上限を食う（約定だけで数えると通ってしまう 2 件目が止まる）。</summary>
    [Fact]
    public async Task 未約定の空売りが1銘柄の上限を食う()
    {
        var ledger = LongHeld("AAPL", 20, 100m);
        var pendingId = Guid.NewGuid();
        var pending = ShortEntry(quantity: 6, price: 25m); // 150（未約定）
        ledger.AppendApproval(pendingId, pending, Now.AddMinutes(-5));
        var clock = new FakeClock(Now, Today);
        var settings = TradingDefaults.CreateSettings();
        var builder = new PortfolioSnapshotBuilder(
            new FakePortfolioStateProvider(new PortfolioState { LedgerEquity = 3_000m }),
            new InMemoryKillSwitchStore(), new InMemoryPauseStore(),
            FakeBrokerAccountObservations.Margin(), FakeInformationDegradation.Affirmed(),
            capitalBaseline: FakeCapitalBaseline.Of(3_000m));
        var service = new OrderScreeningService(
            new InMemoryRiskSettingsStore(settings), builder, new InMemoryLockoutStore(), clock, new WeekendBusinessCalendar(),
            new InMemoryBuyInInferenceStore(), ledger,
            TestShortSellContexts.Supplier(
                new FixedShortSellBorrowSource(ShortSellBorrowObservation.Permitted()), clock, ledger,
                new FixedWorkingEntries([Working("MSFT", TradeSide.Sell, 6, 25m, pendingId)]),
                new FixedCurrentPrices(Prices(("AAPL", 100m)))));

        // 2 件目 7 × 25 = 175。単独なら 175 ≦ 300 だが、未約定の 150 と合わせて 325 > 300。
        var outcome = await service.ScreenAsync(
            new TradeDecisionMade(Guid.NewGuid(), ShortEntry(quantity: 7, price: 25m), "2 件目", Now),
            TestContext.Current.CancellationToken);

        outcome.Rejected!.Reasons.Should().Contain(RejectionReason.ShortExposureExceeded);
    }

    /// <summary>T-10-1023: 借株可否が分からなければ文脈なし＝BorrowUnavailable で打ち切り、上限は評価されない（今と同じ）。</summary>
    [Fact]
    public async Task 借株可否が分からなければ上限は評価されずBorrowUnavailableで拒否される()
    {
        var (service, _) = Screening(ShortSellBorrowObservation.Unknown("query-failed"));

        var outcome = await service.ScreenAsync(
            new TradeDecisionMade(Guid.NewGuid(), ShortEntry(quantity: 10, price: 40m), "テスト判断", Now),
            TestContext.Current.CancellationToken);

        outcome.Rejected!.Reasons.Should().Contain(RejectionReason.BorrowUnavailable)
            .And.NotContain(RejectionReason.ShortExposureExceeded, "文脈が無いときは判定コアが BorrowUnavailable で打ち切る（偽の文脈で上限を評価したことにしない）");
    }

    /// <summary>T-10-1023: 売り建て以外（買い建て・手仕舞い）では借株可否を照会しない（ブローカーの枠を使わない）。</summary>
    [Fact]
    public async Task 売り建て以外では借株可否を照会しない()
    {
        var (service, borrow) = Screening(ShortSellBorrowObservation.Permitted(), LongHeld("AAPL", 20, 100m), Prices(("AAPL", 100m)));

        await service.ScreenAsync(
            new TradeDecisionMade(Guid.NewGuid(), Entry("NVDA", TradeSide.Buy, 1, 100m), "買い建て", Now),
            TestContext.Current.CancellationToken);
        await service.ScreenAsync(
            new TradeDecisionMade(Guid.NewGuid(),
                new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper,
                    20, 100m, PositionEffect.Close), "手仕舞い", Now),
            TestContext.Current.CancellationToken);

        borrow.Calls.Should().Be(0);

        await service.ScreenAsync(
            new TradeDecisionMade(Guid.NewGuid(), ShortEntry(), "売り建て", Now), TestContext.Current.CancellationToken);
        borrow.Calls.Should().Be(1);
    }

    private sealed class FixedWorkingEntries(IReadOnlyList<WorkingEntryOrder> orders) : IWorkingEntryOrderSource
    {
        public IReadOnlyList<WorkingEntryOrder> GetWorkingEntryOrders(DateTimeOffset approvedAtOrAfter) =>
            orders.Where(o => o.ApprovedAt >= approvedAtOrAfter).ToList();
    }
}
