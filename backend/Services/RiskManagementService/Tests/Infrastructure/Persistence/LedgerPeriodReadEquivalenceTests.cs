using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetFills;
using RiskManagementService.Features.RiskManagement.GetOpeningInventory;
using RiskManagementService.Infrastructure.Persistence;
using Xunit;

namespace RiskManagementService.Tests;

// T-06-060・T-06-061, FR-06, FR-16, #1186, IADR-0506 決定 1・3: 台帳の範囲つきの読み口（市場・約定時刻を SQL の条件へ下ろす）と、
// それを使う期間開始時点の在庫・期間の約定が、台帳の全行を畳む従来（GetFills() → 純関数）と一致することを固定する。
// EF 実装（InMemory プロバイダ）とインメモリ実装（ポートの既定実装）の両方で確かめる。
public class LedgerPeriodReadEquivalenceTests
{
    private static readonly DateOnly Center = new(2026, 11, 1); // 米国の夏時間の終了日（EDT → EST）。

    public static TheoryData<string> Stores => new() { "ef", "in-memory" };

    private static IPortfolioLedgerStore NewStore(string kind) =>
        kind == "ef"
            ? new EfPortfolioLedgerStore(new RiskManagementDbContext(
                new DbContextOptionsBuilder<RiskManagementDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options))
            : new InMemoryPortfolioLedgerStore();

    private static int _orderSeq;

    private static void Fill(
        IPortfolioLedgerStore store, string symbol, Market market, TradeSide side, PositionEffect effect,
        int quantity, decimal price, DateTimeOffset at, decimal? recognitionRate = 150m)
    {
        var decisionId = Guid.NewGuid();
        var intent = new OrderIntent(symbol, market, side, ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, price, effect,
            FxRateToBase: market == Market.Japan ? 0.0067m : 1m);
        store.AppendApproval(decisionId, intent, at.AddMinutes(-1), recognitionRate, ApprovalSource.OrderApproved);
        store.AppendFill(decisionId, $"ORD-{Interlocked.Increment(ref _orderSeq)}", quantity, price, at, BrokerProvider.MoomooSimulate)
            .Should().BeTrue();
    }

    private static void Adoption(IPortfolioLedgerStore store, string symbol, Market market, TradeSide side, int quantity, DateTimeOffset at) =>
        store.AppendDriftAdoption(new LedgerDriftAdoption(
            Guid.NewGuid(), symbol, market, side, quantity, 100m, 1m, 100, 100 - quantity, at.AddMinutes(-5), "owner", "test", at))
            .Should().BeTrue();

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    // 取引日の端（ET は EDT の 04:00Z・EST の 05:00Z、JST は 15:00Z）の前後・夏時間の切替・同時刻の売買・取り込み・2 市場を混ぜた台帳。
    private static void Seed(IPortfolioLedgerStore s)
    {
        // 米国 AAPL: ET の取引日の端の両側。
        Fill(s, "AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 100, 200m, Utc(10, 28, 3, 59)); // ET 10-27 23:59（EDT）
        Fill(s, "AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 50, 210m, Utc(10, 28, 4, 0));   // ET 10-28 00:00（EDT）
        Fill(s, "AAPL", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 30, 220m, Utc(11, 1, 4, 59)); // ET 11-01 00:59（EDT 側）
        Fill(s, "AAPL", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 20, 215m, Utc(11, 1, 5, 30)); // ET 11-01 01:30（EST 側の 2 回目の 1 時台）
        Fill(s, "AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 230m, Utc(11, 2, 4, 59));   // ET 11-01 23:59（EST）
        Fill(s, "AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 235m, Utc(11, 2, 5, 0));    // ET 11-02 00:00（EST）
        Adoption(s, "AAPL", Market.UnitedStates, TradeSide.Sell, 15, Utc(10, 30, 3, 0));                          // ET 10-29 23:00

        // 米国 TSLA: 同時刻の売買（並びが畳み込みに効く。全決済 → 反転）。
        Fill(s, "TSLA", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 250m, Utc(10, 29, 15));
        Fill(s, "TSLA", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 10, 260m, Utc(10, 31, 14));
        Fill(s, "TSLA", Market.UnitedStates, TradeSide.Sell, PositionEffect.Open, 5, 260m, Utc(10, 31, 14));

        // 東証 7203: JST の取引日の端の両側（UTC では前日）。未記録の認識時レートも混ぜる。
        Fill(s, "7203", Market.Japan, TradeSide.Buy, PositionEffect.Open, 100, 3000m, Utc(10, 30, 14, 59));      // JST 10-30 23:59
        Fill(s, "7203", Market.Japan, TradeSide.Buy, PositionEffect.Open, 100, 3100m, Utc(10, 30, 15, 0));       // JST 10-31 00:00
        Fill(s, "7203", Market.Japan, TradeSide.Buy, PositionEffect.Open, 100, 3200m, Utc(11, 1, 0, 30), recognitionRate: null);
        Fill(s, "7203", Market.Japan, TradeSide.Sell, PositionEffect.Close, 50, 3300m, Utc(11, 2, 15, 0));       // JST 11-03 00:00
        Adoption(s, "7203", Market.Japan, TradeSide.Sell, 20, Utc(11, 1, 14, 59));                                // JST 11-01 23:59

        // 遠い過去（外包の下限より前）と遠い未来（上限より後）。
        Fill(s, "MSFT", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 7, 400m, Utc(1, 5, 15));
        Fill(s, "MSFT", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 3, 410m, Utc(12, 20, 15));
    }

    private static IEnumerable<DateOnly> Days() =>
        Enumerable.Range(-8, 17).Select(Center.AddDays);

    [Theory]
    [MemberData(nameof(Stores))]
    public void T06_060_範囲つきの読み口は市場と約定時刻で絞り_全行読みの相対順を保つ(string kind)
    {
        var store = NewStore(kind);
        Seed(store);
        var all = store.GetFills();
        DateTimeOffset? lo = Utc(10, 28, 4, 0);
        DateTimeOffset? hi = Utc(11, 2, 4, 59);

        foreach (var market in new Market?[] { null, Market.UnitedStates, Market.Japan })
            foreach (var (from, before) in new[] { (lo, hi), (null, hi), (lo, null), ((DateTimeOffset?)null, (DateTimeOffset?)null) })
            {
                var expected = all.Where(f =>
                    (market is not { } m || f.Market == m)
                    && (from is not { } a || f.ExecutedAt >= a)
                    && (before is not { } b || f.ExecutedAt < b)).ToList();

                store.GetFillsExecutedBetween(market, from, before).Should().Equal(expected, $"market={market} from={from} before={before}");
            }

        // 端: 下限は含み・上限は含まない。取り込み行も同じ時刻（取り込み日時）で絞る。
        var range = store.GetFillsExecutedBetween(Market.UnitedStates, lo, hi);
        range.Should().Contain(f => f.Symbol == "AAPL" && f.ExecutedAt == lo);
        range.Should().NotContain(f => f.ExecutedAt == hi);
        range.Should().Contain(f => f.IsDriftAdoption && f.Symbol == "AAPL");
        range.Should().NotContain(f => f.Market == Market.Japan);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void T06_060_範囲つきの読み口はオフセット付きの境界を瞬間として比べる(string kind)
    {
        var store = NewStore(kind);
        Seed(store);

        // JST 表記の同じ瞬間（UTC 10-28 04:00 = JST 10-28 13:00）。
        var jst = new DateTimeOffset(2026, 10, 28, 13, 0, 0, TimeSpan.FromHours(9));

        store.GetFillsExecutedBetween(Market.UnitedStates, jst, jst.AddTicks(1)).Should().ContainSingle()
            .Which.ExecutedAt.Should().Be(Utc(10, 28, 4, 0));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void T06_061_期間開始時点の在庫は全行を畳む従来と一致する_取引日の端_夏時間_複数市場_同時刻(string kind)
    {
        var store = NewStore(kind);
        Seed(store);
        var all = store.GetFills();

        foreach (var market in new[] { Market.UnitedStates, Market.Japan })
            foreach (var before in Days().Append(DateOnly.MinValue).Append(DateOnly.MaxValue))
            {
                OpeningInventoryQuery.AsOf(store, market, before)
                    .Should().Equal(OpeningInventoryQuery.AsOf(all, market, before), $"market={market} before={before}");
            }

        // 結果が空でない日を少なくとも 1 つ含む（自明な一致〔両方空〕だけで緑にならない）。
        OpeningInventoryQuery.AsOf(store, Market.UnitedStates, Center.AddDays(1)).Should().NotBeEmpty();
        OpeningInventoryQuery.AsOf(store, Market.Japan, Center.AddDays(1)).Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void T06_061_期間の約定は全行を絞る従来と一致する_取引日の端_夏時間_逆順_DateOnly_の端(string kind)
    {
        var store = NewStore(kind);
        Seed(store);
        var all = store.GetFills();
        var days = Days().Append(DateOnly.MinValue).Append(DateOnly.MaxValue).ToList();

        foreach (var from in days)
            foreach (var to in days)
            {
                PeriodFillQuery.InTradingDayRange(store, from, to)
                    .Should().Equal(PeriodFillQuery.InTradingDayRange(all, from, to), $"from={from} to={to}");
            }

        PeriodFillQuery.InTradingDayRange(store, Center.AddDays(-3), Center).Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void T06_061_空の台帳では在庫も期間の約定も空(string kind)
    {
        var store = NewStore(kind);

        store.GetFillsExecutedBetween(null, null, null).Should().BeEmpty();
        OpeningInventoryQuery.AsOf(store, Market.UnitedStates, Center).Should().BeEmpty();
        OpeningInventoryQuery.AsOf(store, Market.Japan, Center).Should().BeEmpty();
        PeriodFillQuery.InTradingDayRange(store, Center.AddDays(-7), Center).Should().BeEmpty();
    }
}
