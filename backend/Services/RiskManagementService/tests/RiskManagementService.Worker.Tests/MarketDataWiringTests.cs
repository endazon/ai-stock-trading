using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Worker.Composable.MarketData;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiStockTrading.RiskManagement.Worker.Tests;

// #81, FR-10, IADR-0066: 現在値の補充（非同期・背景）と読み出し（同期・判定経路）の結線を検証する。
public class MarketDataWiringTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 3, 0, 0, TimeSpan.Zero);

    private static IOptions<MarketDataOptions> Options(int maxStalenessSeconds = 300) =>
        Microsoft.Extensions.Options.Options.Create(new MarketDataOptions { MaxQuoteStalenessSeconds = maxStalenessSeconds });

    private static OpenPosition Position(string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, TradeSide.Buy, 10, 1_000m);

    private static Quote Quote(decimal price, string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, price, Now);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
        public DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);
    }

    private sealed class StubSource(Quote? quote) : IMarketDataSource
    {
        public List<string> Requested { get; } = [];

        public Task<Quote?> GetLatestQuoteAsync(string symbol, Market market, CancellationToken cancellationToken = default)
        {
            Requested.Add(symbol);
            return Task.FromResult(quote is null || quote.Symbol != symbol ? null : quote);
        }
    }

    // --- 読み出し側（同期・判定経路） ---

    [Fact]
    public void 補充済みの現在値は建玉ぶんだけ読み出される()
    {
        var cache = new QuoteCache();
        cache.Set(Quote(1_100m, "AAPL"), Now);
        cache.Set(Quote(2_100m, "MSFT"), Now); // 建玉に無い銘柄は返さない
        var sut = new CachedCurrentPriceSource(cache, new FixedClock(Now), Options());

        var prices = sut.GetCurrentPrices([Position("AAPL")]);

        prices.Should().HaveCount(1);
        prices[("AAPL", Market.UnitedStates)].Should().Be(1_100m);
    }

    [Fact]
    public void 補充されていない建玉はキーを持たない()
    {
        var sut = new CachedCurrentPriceSource(new QuoteCache(), new FixedClock(Now), Options());

        sut.GetCurrentPrices([Position()]).Should().BeEmpty(); // 含みは 0 に倒れる
    }

    [Fact]
    public void 保持期限を超えた前回値は取得不可として落とす()
    {
        var cache = new QuoteCache();
        cache.Set(Quote(1_100m), Now.AddSeconds(-301)); // 期限（300s）超過
        var sut = new CachedCurrentPriceSource(cache, new FixedClock(Now), Options(maxStalenessSeconds: 300));

        sut.GetCurrentPrices([Position()]).Should().BeEmpty();
    }

    [Fact]
    public void 保持期限内の前回値は読み出される()
    {
        var cache = new QuoteCache();
        cache.Set(Quote(1_100m), Now.AddSeconds(-299));
        var sut = new CachedCurrentPriceSource(cache, new FixedClock(Now), Options(maxStalenessSeconds: 300));

        sut.GetCurrentPrices([Position()])[("AAPL", Market.UnitedStates)].Should().Be(1_100m);
    }

    // --- 補充側（非同期・背景） ---

    [Fact]
    public async Task 補充は保有建玉の現在値だけを引いて手元へ入れる()
    {
        var (provider, source) = BuildRefreshHost(Quote(1_100m));
        var cache = provider.GetRequiredService<QuoteCache>();
        var sut = provider.GetRequiredService<QuoteRefreshService>();

        await sut.RunOnceAsync(CancellationToken.None);

        source.Requested.Should().Equal("AAPL"); // 建玉のある銘柄のみ
        cache.GetFresh("AAPL", Market.UnitedStates, Now, TimeSpan.FromMinutes(5))!.Price.Should().Be(1_100m);
    }

    [Fact]
    public async Task 既定の_no_op_ソースでは何も補充されない()
    {
        // 既定（実市況未接続）は取得不可＝手元は空のまま＝含み 0・DD 0（現行挙動）。
        var (provider, _) = BuildRefreshHost(quote: null);
        var cache = provider.GetRequiredService<QuoteCache>();

        await provider.GetRequiredService<QuoteRefreshService>().RunOnceAsync(CancellationToken.None);

        cache.GetFresh("AAPL", Market.UnitedStates, Now, TimeSpan.FromMinutes(5)).Should().BeNull();
    }

    [Fact]
    public async Task 建玉が無ければ現在値を引かない()
    {
        var (provider, source) = BuildRefreshHost(Quote(1_100m), withOpenPosition: false);

        await provider.GetRequiredService<QuoteRefreshService>().RunOnceAsync(CancellationToken.None);

        source.Requested.Should().BeEmpty();
    }

    // 台帳（scoped）＋現在値ソースを組んだ最小のホストを作る。
    private static (ServiceProvider Provider, StubSource Source) BuildRefreshHost(Quote? quote, bool withOpenPosition = true)
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        if (withOpenPosition)
        {
            var decisionId = Guid.NewGuid();
            var intent = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, 10, 1_000m);
            ledger.AppendApproval(decisionId, intent, Now);
            ledger.AppendFill(decisionId, "ORD-1", 10, 1_000m, Now);
        }

        var source = new StubSource(quote);
        var services = new ServiceCollection();
        services.AddScoped<IPortfolioLedgerStore>(_ => ledger);
        services.AddSingleton<IMarketDataSource>(source);
        services.AddSingleton<QuoteCache>();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        services.AddSingleton(Options());
        services.AddSingleton<ILogger<QuoteRefreshService>>(NullLogger<QuoteRefreshService>.Instance);
        services.AddSingleton<QuoteRefreshService>();

        return (services.BuildServiceProvider(), source);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
