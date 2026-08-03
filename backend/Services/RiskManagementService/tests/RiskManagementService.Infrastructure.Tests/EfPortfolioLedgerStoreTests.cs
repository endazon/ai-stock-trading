using AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiStockTrading.RiskManagement.Infrastructure.Tests;

// FR-10, FR-05, IADR-0018: 取引台帳 EF ストアの永続化・相関・冪等を InMemory DB で検証する。
public class EfPortfolioLedgerStoreTests
{
    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static OrderIntent BuyIntent(int qty, decimal price) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, qty, price);

    [Fact]
    public void 承認_Intent_の損切り価格を約定に補完して返す()
    {
        // IADR-0035: 損切り価格（権威データ）が ApprovedOrderRow に永続化され、LedgerFill に補完される。
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();
        var intent = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, 10, 1_000m, PositionEffect.Open, 950m);

        using (var db = NewContext(dbName))
        {
            var store = new EfPortfolioLedgerStore(db);
            store.AppendApproval(decisionId, intent, DateTimeOffset.UtcNow);
            store.AppendFill(decisionId, "ORD-1", 10, 1_050m, DateTimeOffset.UtcNow);
        }

        using var db2 = NewContext(dbName);
        new EfPortfolioLedgerStore(db2).GetFills().Single().StopLossPrice.Should().Be(950m);
    }

    [Fact]
    public void 承認と約定を記録すると相関済みの約定を返す()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            var store = new EfPortfolioLedgerStore(db);
            store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);
            store.AppendFill(decisionId, "ORD-1", 10, 1_050m, DateTimeOffset.UtcNow).Should().BeTrue();
        }

        using var db2 = NewContext(dbName);
        var fills = new EfPortfolioLedgerStore(db2).GetFills();
        fills.Should().HaveCount(1);
        fills[0].Symbol.Should().Be("AAPL");
        fills[0].Side.Should().Be(TradeSide.Buy);
        fills[0].PositionEffect.Should().Be(PositionEffect.Open);
        fills[0].Quantity.Should().Be(10);
        fills[0].Price.Should().Be(1_050m);
    }

    [Fact]
    public void 承認のない約定は記録されず_false_を返す()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);

        store.AppendFill(Guid.NewGuid(), "ORD-X", 10, 1_000m, DateTimeOffset.UtcNow).Should().BeFalse();
        store.GetFills().Should().BeEmpty();
    }

    [Fact]
    public void 同一_OrderId_の再送は重複記録しない()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var decisionId = Guid.NewGuid();
        store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);

        store.AppendFill(decisionId, "ORD-1", 10, 1_000m, DateTimeOffset.UtcNow).Should().BeTrue();
        store.AppendFill(decisionId, "ORD-1", 10, 1_000m, DateTimeOffset.UtcNow).Should().BeTrue(); // 再送

        store.GetFills().Should().HaveCount(1);
    }

    // #270, IADR-0113: 約定数量は累積値。同一 OrderId は累積が増えたときだけ更新する（差分加算しない）。
    [Fact]
    public void 同一_OrderId_の累積約定は一行のまま最新値へ更新される()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var decisionId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        store.AppendApproval(decisionId, BuyIntent(1_000, 340m), t0);

        store.AppendFill(decisionId, "ORD-1", 300, 340.5m, t0.AddSeconds(30)).Should().BeTrue();
        store.AppendFill(decisionId, "ORD-1", 1_000, 340.8m, t0.AddSeconds(60)).Should().BeTrue();

        var fills = store.GetFills();
        fills.Should().HaveCount(1, "1 注文 = 1 行（差分行を追記しない＝二重計上しない）");
        fills[0].Quantity.Should().Be(1_000);
        fills[0].Price.Should().Be(340.8m);
        fills[0].ExecutedAt.Should().Be(t0.AddSeconds(60));
    }

    [Fact]
    public void 少ない数量の後追いでは約定が巻き戻らない()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var decisionId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        store.AppendApproval(decisionId, BuyIntent(1_000, 340m), t0);

        store.AppendFill(decisionId, "ORD-1", 1_000, 340.8m, t0.AddSeconds(60)).Should().BeTrue();
        // 順序が前後して届いた古いスナップショット（部分約定）。
        store.AppendFill(decisionId, "ORD-1", 300, 340.5m, t0.AddSeconds(30)).Should().BeTrue();

        var fills = store.GetFills();
        fills.Should().HaveCount(1);
        fills[0].Quantity.Should().Be(1_000);
        fills[0].Price.Should().Be(340.8m);
        fills[0].ExecutedAt.Should().Be(t0.AddSeconds(60));
    }

    [Fact]
    public void 同一_DecisionId_の承認再送は最初の内容を保持する()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var decisionId = Guid.NewGuid();

        store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);
        store.AppendApproval(decisionId, BuyIntent(999, 9_999m), DateTimeOffset.UtcNow); // 再送（無視）
        store.AppendFill(decisionId, "ORD-1", 10, 1_000m, DateTimeOffset.UtcNow);

        var fills = store.GetFills();
        fills.Should().HaveCount(1);
        fills[0].Quantity.Should().Be(10);
    }
}
