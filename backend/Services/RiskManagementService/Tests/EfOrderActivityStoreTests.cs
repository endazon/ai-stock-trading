using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RiskManagementService.Tests;

// FR-19, #154, IADR-0067: 注文アクティビティ射影の EF ストア/供給源を InMemory DB で検証する。
// 射影の状態遷移仕様は InMemoryOrderActivityStoreTests が権威。ここは EF 版が同じ結果を返すこと（別スコープ含む）を確かめる。
public class EfOrderActivityStoreTests
{
    private static readonly DateTimeOffset Base = new(2026, 7, 18, 6, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lookback = TimeSpan.FromMinutes(5);

    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    [Fact]
    public void 承認から取消までを射影し別コンテキストの供給源から窓として読める()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            IOrderActivityStore store = new EfOrderActivityStore(db);
            store.RecordPlacement(decisionId, "AAPL", Market.UnitedStates, TradeSide.Buy, 10, Base);
            store.RecordModification(decisionId, 6, Base.AddSeconds(10));
            store.RecordCancellation(decisionId, Base.AddSeconds(30));
        }

        using var db2 = NewContext(dbName);
        var source = new EfOrderActivitySource(db2);
        var record = source.GetRecentActivity("AAPL", Market.UnitedStates, Base.AddMinutes(1), Lookback)
            .Records.Should().ContainSingle().Subject;
        record.AmendmentCount.Should().Be(1);
        record.Quantity.Should().Be(6);
        record.Status.Should().Be(OrderStatus.Cancelled);
        record.IsCancelledWithoutFill.Should().BeTrue();
        record.Lifetime.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void 約定は状態と約定数と終端時刻を射影する()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();
        using var db = NewContext(dbName);
        IOrderActivityStore store = new EfOrderActivityStore(db);
        store.RecordPlacement(decisionId, "AAPL", Market.UnitedStates, TradeSide.Buy, 10, Base);
        store.RecordExecution(decisionId, OrderStatus.Filled, 10, Base.AddSeconds(1));

        var record = new EfOrderActivitySource(db)
            .GetRecentActivity("AAPL", Market.UnitedStates, Base.AddMinutes(1), Lookback)
            .Records.Should().ContainSingle().Subject;
        record.Status.Should().Be(OrderStatus.Filled);
        record.FilledQuantity.Should().Be(10);
        record.TerminalAt.Should().Be(Base.AddSeconds(1));
    }

    [Fact]
    public void 承認の再送は既存状態を上書きしない_冪等()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();
        using var db = NewContext(dbName);
        IOrderActivityStore store = new EfOrderActivityStore(db);
        store.RecordPlacement(decisionId, "AAPL", Market.UnitedStates, TradeSide.Buy, 10, Base);
        store.RecordExecution(decisionId, OrderStatus.Filled, 10, Base.AddSeconds(1));
        store.RecordPlacement(decisionId, "AAPL", Market.UnitedStates, TradeSide.Buy, 10, Base);

        var record = new EfOrderActivitySource(db)
            .GetRecentActivity("AAPL", Market.UnitedStates, Base.AddMinutes(1), Lookback)
            .Records.Should().ContainSingle().Subject;
        record.Status.Should().Be(OrderStatus.Filled);
    }

    [Fact]
    public void 相関する承認が無い更新は無視される()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = NewContext(dbName);
        IOrderActivityStore store = new EfOrderActivityStore(db);
        store.RecordExecution(Guid.NewGuid(), OrderStatus.Filled, 10, Base);

        new EfOrderActivitySource(db)
            .GetRecentActivity("AAPL", Market.UnitedStates, Base.AddMinutes(1), Lookback)
            .Records.Should().BeEmpty();
    }

    [Fact]
    public void 窓外や別銘柄は返さない()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = NewContext(dbName);
        IOrderActivityStore store = new EfOrderActivityStore(db);
        store.RecordPlacement(Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 10, Base);
        store.RecordPlacement(Guid.NewGuid(), "MSFT", Market.UnitedStates, TradeSide.Buy, 10, Base);

        var source = new EfOrderActivitySource(db);
        // 別銘柄は混ざらない。
        source.GetRecentActivity("AAPL", Market.UnitedStates, Base.AddMinutes(1), Lookback)
            .Records.Should().ContainSingle();
        // 窓外（asOf-lookback より前の発注）は返さない。
        source.GetRecentActivity("AAPL", Market.UnitedStates, Base.AddMinutes(10), Lookback)
            .Records.Should().BeEmpty();
    }
}
