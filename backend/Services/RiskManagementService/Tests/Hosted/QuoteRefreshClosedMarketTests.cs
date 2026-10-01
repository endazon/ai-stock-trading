using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Common.Abstractions;
using RiskManagementService.Infrastructure.ExternalServices;
using RiskManagementService.Hosted;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace RiskManagementService.Tests;

// FR-01, FR-10, #1131, IADR-0473: 現在値の補充は閉場中は引かない（閉場ごとに 1 回だけ引く）。閉場中に引いた値は
// 次の開場から鮮度を数えるため、閉場中・寄り付き直後も価格を読める（窓の両端。作業仕様書の窓の表 P1〜P6）。
//
// 時刻は 2026-09-30（水）〜10-01（木）。米国は夏時間（EDT＝UTC−4）で 13:30–20:00 UTC が場中、東証は 00:00–02:30 /
// 03:30–06:30 UTC が場中。開場判定は共有カーネルの MarketHours（市場監視と同じ実体）。
public class QuoteRefreshClosedMarketTests
{
    private static readonly DateTimeOffset UsOpenDay = new(2026, 9, 30, 14, 0, 0, TimeSpan.Zero);   // 10:00 EDT・場中
    private static readonly DateTimeOffset UsClose = new(2026, 9, 30, 20, 0, 0, TimeSpan.Zero);     // 16:00 EDT・引け
    private static readonly DateTimeOffset UsNextOpen = new(2026, 10, 1, 13, 30, 0, TimeSpan.Zero); // 翌 9:30 EDT
    private static readonly DateTimeOffset JpOpenUsClosed = new(2026, 10, 1, 1, 0, 0, TimeSpan.Zero); // 10:00 JST / 21:00 EDT

    private static readonly TimeSpan MaxStaleness = TimeSpan.FromSeconds(300);

    private sealed class CountingSource : IMarketDataSource
    {
        public List<(string Symbol, DateTimeOffset At)> Requested { get; } = [];

        public TimeProvider? Clock { get; set; }

        public bool Unavailable { get; set; }

        public Task<Quote?> GetLatestQuoteAsync(string symbol, Market market, CancellationToken cancellationToken = default)
        {
            var at = Clock!.GetUtcNow();
            Requested.Add((symbol, at));
            return Task.FromResult(Unavailable ? null : new Quote(symbol, market, 100m + Requested.Count, at));
        }
    }

    private sealed class FixedClock(Func<DateTimeOffset> now) : IClock
    {
        public DateTimeOffset UtcNow => now();
        public DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);
    }

    private sealed record Host(QuoteRefreshService Sut, CountingSource Source, QuoteCache Cache, StubTimeProvider Time)
    {
        public CachedCurrentPriceSource Reader() =>
            new(Cache, new FixedClock(() => Time.Now), Microsoft.Extensions.Options.Options.Create(
                new MarketDataOptions { MaxQuoteStalenessSeconds = (int)MaxStaleness.TotalSeconds }));

        public int Count(string symbol) => Source.Requested.Count(r => r.Symbol == symbol);

        public async Task TickAsync(DateTimeOffset at)
        {
            Time.Now = at;
            await Sut.RunOnceAsync(CancellationToken.None);
        }
    }

    private static OpenPosition Us(string symbol = "AAPL") => new(symbol, Market.UnitedStates, TradeSide.Buy, 10, 100m);

    private static OpenPosition Jp(string symbol = "7203") => new(symbol, Market.Japan, TradeSide.Buy, 100, 2_000m);

    private static Host Build(DateTimeOffset now, params (string Symbol, Market Market)[] holdings)
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        foreach (var (symbol, market) in holdings)
        {
            var decisionId = Guid.NewGuid();
            var intent = new OrderIntent(symbol, market, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 100m);
            ledger.AppendApproval(decisionId, intent, now.AddDays(-2));
            ledger.AppendFill(decisionId, "ORD-" + symbol, 10, 100m, now.AddDays(-2));
        }

        var time = new StubTimeProvider(now);
        var source = new CountingSource { Clock = time };
        var cache = new QuoteCache();
        var services = new ServiceCollection();
        services.AddScoped<IPortfolioLedgerStore>(_ => ledger);
        services.AddSingleton<IMarketDataSource>(source);
        services.AddSingleton(cache);
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new MarketDataOptions()));
        services.AddSingleton<ILogger<QuoteRefreshService>>(NullLogger<QuoteRefreshService>.Instance);
        services.AddSingleton<QuoteRefreshService>();
        var sut = services.BuildServiceProvider().GetRequiredService<QuoteRefreshService>();
        return new Host(sut, source, cache, time);
    }

    // T-10-1960, FR-10, #1131: 開場中は毎巡回引く（手元に新しい値があっても引く＝場中の補充は従来どおり）。
    [Fact]
    public async Task 開場中は毎巡回現在値を引く()
    {
        var host = Build(UsOpenDay, ("AAPL", Market.UnitedStates));

        await host.TickAsync(UsOpenDay);
        await host.TickAsync(UsOpenDay.AddMinutes(1));
        await host.TickAsync(UsClose.AddSeconds(-1)); // 引けの 1 秒前は場中

        host.Count("AAPL").Should().Be(3);
    }

    // 🔴 T-10-1961, FR-01, #1131: 引けから翌朝の寄り付き前まで 1 分ごとに巡回しても、引くのは引けの後の 1 回だけ（窓の表 P1）。
    // 是正前は約 1,050 回（引け〜寄り付きの分数）だった。
    [Fact]
    public async Task 閉場中は引けの後の1回だけ引き以後は引かない()
    {
        var host = Build(UsOpenDay, ("AAPL", Market.UnitedStates), ("MSFT", Market.UnitedStates));
        await host.TickAsync(UsClose.AddMinutes(-1)); // 場中の最後の巡回

        for (var at = UsClose; at < UsNextOpen; at = at.AddMinutes(1))
            await host.TickAsync(at);

        host.Source.Requested.Where(r => r.At >= UsClose).Should().HaveCount(2, "閉場ごとに銘柄ごと 1 回");
        host.Source.Requested.Where(r => r.At >= UsClose).Select(r => r.At).Distinct().Should().Equal(UsClose);
    }

    // T-10-1962, FR-10, #1131: 寄り付き後の最初の巡回で引く（窓の表 P6）。閉場中の値が手元にあっても引く。
    [Fact]
    public async Task 寄り付き後の最初の巡回で引く()
    {
        var host = Build(UsOpenDay, ("AAPL", Market.UnitedStates));
        await host.TickAsync(UsClose.AddMinutes(5));          // 閉場中の 1 回
        await host.TickAsync(UsNextOpen.AddSeconds(-1));      // 寄り付きの 1 秒前: 引かない
        host.Count("AAPL").Should().Be(1);

        await host.TickAsync(UsNextOpen);                     // 寄り付きちょうど（開始は包含）

        host.Count("AAPL").Should().Be(2);
        host.Cache.GetEntry("AAPL", Market.UnitedStates)!.Value.FetchedAt.Should().Be(UsNextOpen);
    }

    // 🔴 T-10-1963, FR-10, #1131: 閉場中に引いた値は夜通し・寄り付き直後も読める（P2・P3）が、寄り付きから保持期限を
    // 超えたら読めない（P4＝夜の値を開場後に信じ続けない）。
    [Fact]
    public async Task 閉場中に引いた値は次の開場から保持期限まで読める()
    {
        var host = Build(UsOpenDay, ("AAPL", Market.UnitedStates));
        await host.TickAsync(UsClose.AddMinutes(1));
        var reader = host.Reader();

        host.Time.Now = UsClose.AddHours(3);                                      // P2
        reader.GetCurrentPrices([Us()]).Should().ContainKey(("AAPL", Market.UnitedStates));
        host.Time.Now = UsNextOpen.AddSeconds(30);                                // P3
        reader.GetCurrentPrices([Us()]).Should().ContainKey(("AAPL", Market.UnitedStates));
        host.Time.Now = UsNextOpen + MaxStaleness;                                // 境界ちょうどは読める
        reader.GetCurrentPrices([Us()]).Should().ContainKey(("AAPL", Market.UnitedStates));
        host.Time.Now = UsNextOpen + MaxStaleness + TimeSpan.FromSeconds(1);      // P4
        reader.GetCurrentPrices([Us()]).Should().BeEmpty();
    }

    // 🔴 T-10-1964, FR-10, #1131: 場中に引いた値の鮮度は従来どおり取得時刻から数える（P5。否定形）。
    // 「起点を常に次の開場にする」形だと、場中の値を翌朝まで信じてしまう。
    [Fact]
    public async Task 場中に引いた値は取得時刻から保持期限で切れる()
    {
        var host = Build(UsOpenDay, ("AAPL", Market.UnitedStates));
        await host.TickAsync(UsOpenDay);
        var reader = host.Reader();

        host.Time.Now = UsOpenDay + MaxStaleness;
        reader.GetCurrentPrices([Us()]).Should().ContainKey(("AAPL", Market.UnitedStates));
        host.Time.Now = UsOpenDay + MaxStaleness + TimeSpan.FromSeconds(1);
        reader.GetCurrentPrices([Us()]).Should().BeEmpty();
    }

    // 🔴 T-10-1965, FR-10, #1131: 引けの直前に引いた値しか無いまま閉場したら、閉場中の最初の巡回で 1 回引く
    // （引かないと、引けの 5 分後から朝まで価格を失う＝窓の表の「両端だが引け後の 1 回を引かない」形）。
    [Fact]
    public async Task 場中の値しか無ければ閉場中の最初の巡回で引き夜通し読める()
    {
        var host = Build(UsOpenDay, ("AAPL", Market.UnitedStates));
        await host.TickAsync(UsClose.AddSeconds(-30));
        await host.TickAsync(UsClose.AddSeconds(30));

        host.Count("AAPL").Should().Be(2);
        host.Time.Now = UsClose.AddHours(6);
        host.Reader().GetCurrentPrices([Us()]).Should().ContainKey(("AAPL", Market.UnitedStates));
    }

    // T-10-1966, FR-10, #1131: 市場ごとに判定する。米国が閉場・東証が場中の時刻では、東証の銘柄は毎巡回引き、
    // 米国の銘柄は閉場中の 1 回だけ引く。東証の昼休み（11:30–12:30 JST）も閉場として扱う。
    [Fact]
    public async Task 開場判定は建玉の市場ごとに行う()
    {
        var host = Build(JpOpenUsClosed, ("AAPL", Market.UnitedStates), ("7203", Market.Japan));

        await host.TickAsync(JpOpenUsClosed);
        await host.TickAsync(JpOpenUsClosed.AddMinutes(1));
        await host.TickAsync(JpOpenUsClosed.AddMinutes(2));

        host.Count("7203").Should().Be(3);
        host.Count("AAPL").Should().Be(1);

        var lunch = new DateTimeOffset(2026, 10, 1, 2, 45, 0, TimeSpan.Zero); // 11:45 JST
        await host.TickAsync(lunch);
        await host.TickAsync(lunch.AddMinutes(1));
        host.Count("7203").Should().Be(4, "昼休みは閉場＝1 回だけ");
        host.Time.Now = lunch.AddMinutes(30);
        host.Reader().GetCurrentPrices([Jp()]).Should().ContainKey(("7203", Market.Japan));
        MarketHours.IsOpen(Market.Japan, lunch).Should().BeFalse();
    }

    // T-10-1967, FR-10, #1131: 閉場中に取得できなかった（null）なら次の巡回でもう一度引く（取得できるまで）。
    // 取得できた後は引かない。閉場中の再起動（手元が空）でも 1 回引く。
    [Fact]
    public async Task 閉場中の取得失敗は次の巡回で引き直し再起動後も1回引く()
    {
        var host = Build(UsClose.AddHours(2), ("AAPL", Market.UnitedStates));
        host.Source.Unavailable = true;
        await host.TickAsync(UsClose.AddHours(2));
        await host.TickAsync(UsClose.AddHours(2).AddMinutes(1));
        host.Count("AAPL").Should().Be(2);

        host.Source.Unavailable = false;
        await host.TickAsync(UsClose.AddHours(2).AddMinutes(2));
        await host.TickAsync(UsClose.AddHours(2).AddMinutes(3));
        host.Count("AAPL").Should().Be(3);
    }

    // T-10-1968, FR-10, #1131: 金曜の引け後に引いた値は週末を通して読め、週末は引かない。月曜の寄り付き後に引く。
    [Fact]
    public async Task 週末は引かず金曜の値を月曜の寄り付きまで読める()
    {
        var fridayClose = new DateTimeOffset(2026, 10, 2, 20, 0, 0, TimeSpan.Zero);
        var mondayOpen = new DateTimeOffset(2026, 10, 5, 13, 30, 0, TimeSpan.Zero);
        var host = Build(fridayClose, ("AAPL", Market.UnitedStates));

        await host.TickAsync(fridayClose.AddMinutes(1));
        await host.TickAsync(fridayClose.AddDays(1));                 // 土曜
        await host.TickAsync(fridayClose.AddDays(2));                 // 日曜
        host.Count("AAPL").Should().Be(1);
        host.Time.Now = mondayOpen.AddSeconds(-1);
        host.Reader().GetCurrentPrices([Us()]).Should().ContainKey(("AAPL", Market.UnitedStates));

        await host.TickAsync(mondayOpen.AddSeconds(10));
        host.Count("AAPL").Should().Be(2);
    }

    // T-10-1969, FR-01, ADR-0043 決定 3, #1131: 日次要求量の見積りは市場監視と同じく米国の場中（390 分）で数える。
    [Fact]
    public void 日次見積りは場中の分数で数える()
    {
        QuoteRefreshService.ActiveMinutesPerDay.Should().Be(MarketSessions.RegularSessionMinutes(Market.UnitedStates));
        QuoteRefreshService.ActiveMinutesPerDay.Should().Be(390);
        MarketDataSourceFactory.EstimateDailyVolume(
                new MarketDataOptions { Finnhub = new FinnhubMarketDataOptions { EstimatedSymbolCount = 3 } },
                60,
                QuoteRefreshService.ActiveMinutesPerDay)
            .Should().Be(3 * 390, "3 銘柄 × 60 秒巡回 × 390 分（是正前は 24 時間で 3 × 1,440）");
    }
}
