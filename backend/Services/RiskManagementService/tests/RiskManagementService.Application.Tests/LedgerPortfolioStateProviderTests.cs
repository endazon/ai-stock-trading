using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-10, FR-05, IADR-0018: 台帳ストア（承認→約定の相関・冪等）と、それを射影するプロバイダの検証。
public class LedgerPortfolioStateProviderTests
{
    private static OrderIntent BuyIntent(int qty, decimal price) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, qty, price);

    [Fact]
    public void 承認を記録した約定は射影に反映される()
    {
        var store = new InMemoryPortfolioLedgerStore();
        var decisionId = Guid.NewGuid();
        store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);
        var appended = store.AppendFill(decisionId, "ORD-1", 10, 1_000m, DateTimeOffset.UtcNow);

        appended.Should().BeTrue();

        var provider = new LedgerPortfolioStateProvider(store, new FixedClock());
        var state = provider.GetCurrent();

        state.OpenPositionCount.Should().Be(1);
        state.InvestedCapital.Should().Be(10_000m);
        state.Capital.Should().Be(TradingDefaults.InitialCapital);
    }

    [Fact]
    public void 承認のない約定は記録されず_false_を返す()
    {
        var store = new InMemoryPortfolioLedgerStore();
        var appended = store.AppendFill(Guid.NewGuid(), "ORD-X", 10, 1_000m, DateTimeOffset.UtcNow);

        appended.Should().BeFalse();
        store.GetFills().Should().BeEmpty();
    }

    [Fact]
    public void 同一_OrderId_の再送は重複記録しない()
    {
        var store = new InMemoryPortfolioLedgerStore();
        var decisionId = Guid.NewGuid();
        store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);
        store.AppendFill(decisionId, "ORD-1", 10, 1_000m, DateTimeOffset.UtcNow);
        store.AppendFill(decisionId, "ORD-1", 10, 1_000m, DateTimeOffset.UtcNow); // 再送

        store.GetFills().Should().HaveCount(1);
    }

    [Fact]
    public void 同一_DecisionId_の承認再送は重複しない()
    {
        var store = new InMemoryPortfolioLedgerStore();
        var decisionId = Guid.NewGuid();
        store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);
        store.AppendApproval(decisionId, BuyIntent(999, 9_999m), DateTimeOffset.UtcNow); // 再送（無視される）
        store.AppendFill(decisionId, "ORD-1", 10, 1_000m, DateTimeOffset.UtcNow);

        var fills = store.GetFills();
        fills.Should().HaveCount(1);
        fills[0].Quantity.Should().Be(10);
    }

    // --- 時価評価の結線（#81, IADR-0066）。既定（現在値ソース未注入＝ゲート OFF）は現行どおり含み 0・DD 0 ---

    // 建玉 10 @1,000 を保有し、当日より前に +2,000 の実現損益を確定した台帳を作る。
    // 当日開始基準 = 初期資金 + 2,000、実現ベースのピーク = 初期資金 + 2,000。
    private static InMemoryPortfolioLedgerStore LedgerWithOpenPosition()
    {
        var store = new InMemoryPortfolioLedgerStore();
        var past = new DateTimeOffset(2026, 7, 8, 3, 0, 0, TimeSpan.Zero);

        var closed = Guid.NewGuid();
        store.AppendApproval(closed, BuyIntent(10, 1_000m), past);
        store.AppendFill(closed, "ORD-A", 10, 1_000m, past);
        var sold = Guid.NewGuid();
        store.AppendApproval(sold, SellIntent(10, 1_200m), past);
        store.AppendFill(sold, "ORD-B", 10, 1_200m, past); // +2,000 実現（当日より前）

        var open = Guid.NewGuid();
        store.AppendApproval(open, BuyIntent(10, 1_000m), past);
        store.AppendFill(open, "ORD-C", 10, 1_000m, past); // 建玉 10 @1,000（現存）

        return store;
    }

    private static OrderIntent SellIntent(int qty, decimal price) =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper, qty, price);

    [Fact]
    public void 現在値ソース未注入なら含み損益とドローダウンは0のまま()
    {
        var provider = new LedgerPortfolioStateProvider(LedgerWithOpenPosition(), new FixedClock());

        var state = provider.GetCurrent();

        state.UnrealizedPnl.Should().Be(0m);
        state.DrawdownRatio.Should().Be(0m);
    }

    [Fact]
    public void 現在値が供給されれば含み損益を時価で算出する()
    {
        // 建玉 10 @1,000 が 1,100 → 含み +1,000。
        var prices = new FakeCurrentPriceSource(new() { [("AAPL", Market.UnitedStates)] = 1_100m });
        var provider = new LedgerPortfolioStateProvider(LedgerWithOpenPosition(), new FixedClock(), prices);

        provider.GetCurrent().UnrealizedPnl.Should().Be(1_000m);
    }

    [Fact]
    public void 含み損が生じればピークからのドローダウンを算出する()
    {
        // ピーク = 初期資金 + 2,000（実現）。建玉 10 @1,000 が 900 → 含み −1,000 → 現在 = ピーク − 1,000。
        var prices = new FakeCurrentPriceSource(new() { [("AAPL", Market.UnitedStates)] = 900m });
        var provider = new LedgerPortfolioStateProvider(LedgerWithOpenPosition(), new FixedClock(), prices);

        var state = provider.GetCurrent();
        var peak = TradingDefaults.InitialCapital + 2_000m;

        state.UnrealizedPnl.Should().Be(-1_000m);
        state.DrawdownRatio.Should().Be(1_000m / peak);
    }

    [Fact]
    public void 含み益で最高値を更新している間はドローダウンは0()
    {
        var prices = new FakeCurrentPriceSource(new() { [("AAPL", Market.UnitedStates)] = 1_100m });
        var provider = new LedgerPortfolioStateProvider(LedgerWithOpenPosition(), new FixedClock(), prices);

        provider.GetCurrent().DrawdownRatio.Should().Be(0m);
    }

    [Fact]
    public void 現在値が取得できない建玉は含み0として扱いドローダウンも出さない()
    {
        // ソースは注入されているが値が空（市況断・TTL 超過）→ 含み 0。実現ベースのピーク＝現在エクイティで DD 0。
        var provider = new LedgerPortfolioStateProvider(LedgerWithOpenPosition(), new FixedClock(), new FakeCurrentPriceSource([]));

        var state = provider.GetCurrent();

        state.UnrealizedPnl.Should().Be(0m);
        state.DrawdownRatio.Should().Be(0m);
    }

    private sealed class FakeCurrentPriceSource(Dictionary<(string Symbol, Market Market), decimal> prices) : ICurrentPriceSource
    {
        public IReadOnlyDictionary<(string Symbol, Market Market), decimal> GetCurrentPrices(IReadOnlyList<OpenPosition> positions) => prices;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 7, 10, 3, 0, 0, TimeSpan.Zero);
        public DateOnly Today => new(2026, 7, 10);
    }
}
