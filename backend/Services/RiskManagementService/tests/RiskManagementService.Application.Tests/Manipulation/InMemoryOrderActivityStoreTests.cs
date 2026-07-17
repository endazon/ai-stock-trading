using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Domain.Manipulation;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests.Manipulation;

// FR-19, #154, IADR-0067: 注文アクティビティ射影ストア（インメモリ）の射影と読み取りを検証する。
// EfOrderActivityStore/EfOrderActivitySource と射影ロジックを共有するため、本テストが射影仕様の権威になる。
public class InMemoryOrderActivityStoreTests
{
    private static readonly DateTimeOffset Base = new(2026, 7, 18, 6, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lookback = TimeSpan.FromMinutes(5);

    private static OrderActivityWindow Window(InMemoryOrderActivityStore store, DateTimeOffset asOf) =>
        store.GetRecentActivity("AAPL", Market.UnitedStates, asOf, Lookback);

    [Fact]
    public void 承認は受付状態の行として射影される()
    {
        var store = new InMemoryOrderActivityStore();
        var decisionId = Guid.NewGuid();
        store.RecordPlacement(decisionId, "AAPL", Market.UnitedStates, TradeSide.Buy, 10, Base);

        var record = Window(store, Base).Records.Should().ContainSingle().Subject;
        record.PlacedAt.Should().Be(Base);
        record.Side.Should().Be(TradeSide.Buy);
        record.Quantity.Should().Be(10);
        record.FilledQuantity.Should().Be(0);
        record.Status.Should().Be(OrderStatus.Accepted);
        record.AmendmentCount.Should().Be(0);
        record.TerminalAt.Should().BeNull();
        record.IsCancelledWithoutFill.Should().BeFalse();
    }

    [Fact]
    public void 約定は状態と約定数と終端時刻を更新する()
    {
        var store = new InMemoryOrderActivityStore();
        var decisionId = Guid.NewGuid();
        store.RecordPlacement(decisionId, "AAPL", Market.UnitedStates, TradeSide.Buy, 10, Base);
        store.RecordExecution(decisionId, OrderStatus.Filled, 10, Base.AddSeconds(1));

        var record = Window(store, Base.AddMinutes(1)).Records.Should().ContainSingle().Subject;
        record.Status.Should().Be(OrderStatus.Filled);
        record.FilledQuantity.Should().Be(10);
        record.TerminalAt.Should().Be(Base.AddSeconds(1));
        record.IsFilledOrPartial.Should().BeTrue();
    }

    [Fact]
    public void 訂正は訂正回数を増やし数量を更新する()
    {
        var store = new InMemoryOrderActivityStore();
        var decisionId = Guid.NewGuid();
        store.RecordPlacement(decisionId, "AAPL", Market.UnitedStates, TradeSide.Buy, 10, Base);
        store.RecordModification(decisionId, 6, Base.AddSeconds(1));
        store.RecordModification(decisionId, 4, Base.AddSeconds(2));

        var record = Window(store, Base.AddMinutes(1)).Records.Should().ContainSingle().Subject;
        record.AmendmentCount.Should().Be(2);
        record.Quantity.Should().Be(4);
    }

    [Fact]
    public void 取消は約定なし取消として射影され生存時間を持つ()
    {
        var store = new InMemoryOrderActivityStore();
        var decisionId = Guid.NewGuid();
        store.RecordPlacement(decisionId, "AAPL", Market.UnitedStates, TradeSide.Buy, 10, Base);
        store.RecordCancellation(decisionId, Base.AddSeconds(30));

        var record = Window(store, Base.AddMinutes(1)).Records.Should().ContainSingle().Subject;
        record.Status.Should().Be(OrderStatus.Cancelled);
        record.IsCancelledWithoutFill.Should().BeTrue();
        record.Lifetime.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void 承認の再送は無視される_冪等()
    {
        var store = new InMemoryOrderActivityStore();
        var decisionId = Guid.NewGuid();
        store.RecordPlacement(decisionId, "AAPL", Market.UnitedStates, TradeSide.Buy, 10, Base);
        store.RecordExecution(decisionId, OrderStatus.Filled, 10, Base.AddSeconds(1));
        // 承認の再送。既存の約定状態を上書きしない。
        store.RecordPlacement(decisionId, "AAPL", Market.UnitedStates, TradeSide.Buy, 10, Base);

        var record = Window(store, Base.AddMinutes(1)).Records.Should().ContainSingle().Subject;
        record.Status.Should().Be(OrderStatus.Filled);
    }

    [Fact]
    public void 相関する承認が無い約定訂正取消は無視される()
    {
        var store = new InMemoryOrderActivityStore();
        var unknown = Guid.NewGuid();
        store.RecordExecution(unknown, OrderStatus.Filled, 10, Base);
        store.RecordModification(unknown, 5, Base);
        store.RecordCancellation(unknown, Base);

        Window(store, Base.AddMinutes(1)).Records.Should().BeEmpty();
    }

    [Fact]
    public void 窓外の発注は返さない()
    {
        var store = new InMemoryOrderActivityStore();
        store.RecordPlacement(Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 10, Base);

        // Base は asOf-lookback より前になる。
        Window(store, Base.AddMinutes(10)).Records.Should().BeEmpty();
    }

    [Fact]
    public void 別銘柄別市場は混ざらない()
    {
        var store = new InMemoryOrderActivityStore();
        store.RecordPlacement(Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 10, Base);
        store.RecordPlacement(Guid.NewGuid(), "MSFT", Market.UnitedStates, TradeSide.Buy, 10, Base);
        store.RecordPlacement(Guid.NewGuid(), "AAPL", Market.Japan, TradeSide.Buy, 10, Base);

        Window(store, Base.AddMinutes(1)).Records.Should().ContainSingle();
    }

    [Fact]
    public void 記録が無ければ空窓を返す()
    {
        var store = new InMemoryOrderActivityStore();

        var window = Window(store, Base);
        window.Records.Should().BeEmpty();
        window.Symbol.Should().Be("AAPL");
        window.AsOf.Should().Be(Base);
    }
}
