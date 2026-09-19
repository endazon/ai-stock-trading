using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, #292, IADR-0117: 処理中（承認済み・未約定）の決済数量。Application 側の
// PortfolioLedgerInFlightCloseTests（InMemory 実装）と同一の観点を EF 実装でも固定し、両実装の乖離を検知する。
public class EfPortfolioLedgerInFlightCloseTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Window = Now.AddMinutes(-30);

    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static Guid Approve(
        EfPortfolioLedgerStore store,
        PositionEffect effect,
        int quantity,
        DateTimeOffset approvedAt,
        string symbol = "AAPL",
        Market market = Market.UnitedStates)
    {
        var decisionId = Guid.NewGuid();
        store.AppendApproval(
            decisionId,
            new OrderIntent(symbol, market, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper,
                quantity, 21m, effect, StopLossPrice: null, FxRateToBase: 1m),
            approvedAt);
        return decisionId;
    }

    [Fact]
    public void 承認も約定も無ければゼロ()
    {
        using var db = NewContext(Guid.NewGuid().ToString());

        new EfPortfolioLedgerStore(db)
            .GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 未約定の決済承認を数える()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(60);
    }

    [Fact]
    public void 複数の未約定決済を合計する()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
        Approve(store, PositionEffect.Close, 15, Now.AddMinutes(-1));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(75);
    }

    [Fact]
    public void 部分約定は未約定ぶんだけを数える()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
        store.AppendFill(id, "ORD-1", 20, 21m, Now.AddMinutes(-4));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(40);
    }

    [Fact]
    public void 全量約定した決済は数えない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
        store.AppendFill(id, "ORD-1", 60, 21m, Now.AddMinutes(-4));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 約定が承認数量を超えても負にはしない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
        store.AppendFill(id, "ORD-1", 70, 21m, Now.AddMinutes(-4));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 新規建ての承認は数えない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        Approve(store, PositionEffect.Open, 60, Now.AddMinutes(-5));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 窓より前に承認された決済は数えない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-31));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 別銘柄と別市場は数えない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5), symbol: "MSFT");
        Approve(store, PositionEffect.Close, 30, Now.AddMinutes(-5), market: Market.Japan);

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // --- #848: 終端になった承認を除く（InMemory 実装の同名テストと同一の観点） ---

    // T-10-400, #848: 稼働環境の実測そのもの（利用者が moomoo アプリで取り消した手仕舞い）。
    [Fact]
    public void 取消が確認できた決済承認は数えない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 3_381, Now.AddMinutes(-5));
        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(3_381);

        store.MarkTerminal(id, OrderStatus.Cancelled, Now.AddMinutes(-1));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // T-10-401, #848: 終端は 3 値とも同じ扱い（取消・失効・拒否）。
    [Theory]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    [InlineData(OrderStatus.Rejected)]
    public void 終端になった決済承認は状態を問わず数えない(OrderStatus terminal)
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));

        store.MarkTerminal(id, terminal, Now.AddMinutes(-1));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // T-10-401, #848: 部分約定のまま取消された承認は丸ごと除く（残りは二度と約定しない）。
    [Fact]
    public void 部分約定のまま取消された承認は残数量も数えない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
        store.AppendFill(id, "ORD-1", 20, 21m, Now.AddMinutes(-4));
        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(40);

        store.MarkTerminal(id, OrderStatus.Cancelled, Now.AddMinutes(-1));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // 🔴 T-10-402, #848（否定形・最重要）: 非終端の状態で終端を捏造しない（除外し過ぎるとショート化する）。
    [Theory]
    [InlineData(OrderStatus.Accepted)]
    [InlineData(OrderStatus.PartiallyFilled)]
    public void 非終端の状態では処理中のままにする(OrderStatus pending)
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));

        store.MarkTerminal(id, pending, Now.AddMinutes(-1));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(60);
    }

    // T-10-405, #848: 単調・冪等（後着の非終端で戻らない）。
    [Fact]
    public void 終端は単調で後着の非終端では戻らない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
        store.MarkTerminal(id, OrderStatus.Cancelled, Now.AddMinutes(-2));

        store.MarkTerminal(id, OrderStatus.Accepted, Now.AddMinutes(-1));
        store.MarkTerminal(id, OrderStatus.Cancelled, Now.AddMinutes(-1));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // 🔴 T-10-405, #848（否定形）: 相関する承認が無い終端は書かない。後着の承認は処理中として数える（安全側）。
    [Fact]
    public void 承認より先に届いた終端は記録しない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var decisionId = Guid.NewGuid();

        store.MarkTerminal(decisionId, OrderStatus.Cancelled, Now.AddMinutes(-5));
        store.AppendApproval(
            decisionId,
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.InternalPaper, 60, 21m, PositionEffect.Close, StopLossPrice: null, FxRateToBase: 1m),
            Now.AddMinutes(-4));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(60);
    }

    // T-10-400, #848: 終端は永続化される（別コンテキストで読み直しても在庫が戻ったまま）。
    [Fact]
    public void 終端は別コンテキストで読み直しても残る()
    {
        var dbName = Guid.NewGuid().ToString();
        Guid id;
        using (var db = NewContext(dbName))
        {
            var store = new EfPortfolioLedgerStore(db);
            id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
            store.MarkTerminal(id, OrderStatus.Cancelled, Now.AddMinutes(-1));
        }

        using var db2 = NewContext(dbName);
        new EfPortfolioLedgerStore(db2)
            .GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // 🔴 T-10-406, #848（監査ブロッキング B1・否定形）: **全量約定は在庫解放の終端ではない**
    //（InMemory 実装の同名テストと同一の観点）。EF 実装では MarkTerminal と AppendFill が別々の
    // SaveChanges であるため、Filled を終端に入れると建玉が丸ごと空いて見える区間が実在する。
    [Fact]
    public void 全量約定の終端は記録せず処理中のままにする()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));

        store.MarkTerminal(id, OrderStatus.Filled, Now.AddMinutes(-1));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(60);

        // 約定が載れば自然に 0 になる（＝Filled を終端に入れる必要がそもそも無い）。
        store.AppendFill(id, "ORD-1", 60, 21m, Now.AddMinutes(-1));
        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // T-10-406, #848: 全量約定を無視しても門を閉じ切らない —— そのあとに本物の終端（取消）が来れば記録する。
    [Fact]
    public void 全量約定を無視した後でも本物の終端は記録する()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));

        store.MarkTerminal(id, OrderStatus.Filled, Now.AddMinutes(-2));
        store.MarkTerminal(id, OrderStatus.Cancelled, Now.AddMinutes(-1));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 別コンテキストで読み直しても同じ結果になる()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            var store = new EfPortfolioLedgerStore(db);
            var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
            store.AppendFill(id, "ORD-1", 20, 21m, Now.AddMinutes(-4));
        }

        using var db2 = NewContext(dbName);
        new EfPortfolioLedgerStore(db2)
            .GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(40);
    }
}
