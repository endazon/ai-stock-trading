using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-860・T-10-861, FR-10, FR-05, #958, IADR-0406 決定1・決定3: 約定追跡が S0 の逆指値レグを追跡上限の外でも
// 拾うための 2 つの口（注文 ID 指定の抽出・追跡の起点の付け直し）の意味論を、**本番の DB 実装とインメモリ実装の両方で
// 同一に**固定する（片側だけの乖離を検知する。OrderReservationForgoneStoreTests と同じ作法）。
// EF 側は呼び出しごとに**別のコンテキスト**で開く（ガードと約定追跡は別々のスコープで同じ行を読み書きする）。
public class ExecutedOrderStoreStopLegTrackingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 6, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Implementations() => ["ef", "inmemory"];

    // 呼ぶたびに store を返す（EF は毎回新しいコンテキスト、インメモリは同じインスタンス）。
    private static Func<IExecutedOrderStore> Stores(string kind)
    {
        if (kind == "inmemory")
        {
            var shared = new InMemoryExecutedOrderStore();
            return () => shared;
        }

        var dbName = Guid.NewGuid().ToString();
        return () => new EfExecutedOrderStore(new OrderExecutionDbContext(
            new DbContextOptionsBuilder<OrderExecutionDbContext>().UseInMemoryDatabase(dbName).Options));
    }

    private static ExecutionRecord Record(string orderId, OrderStatus status, DateTimeOffset at, int filled = 0) =>
        new(Guid.NewGuid(), orderId, "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, 10, 950m, filled, filled > 0 ? 949.5m : 0m, status, 0m, at);

    // T-10-860: 指定した注文 ID の非終端だけを古い順に返す。追跡上限（時刻）は見ない。
    [Theory]
    [MemberData(nameof(Implementations))]
    public void 注文ID指定の抽出は指定したIDの非終端だけを古い順に返し時刻では切らない(string kind)
    {
        var store = Stores(kind);
        store().Save(Record("old-leg", OrderStatus.Accepted, Now.AddDays(-3)));
        store().Save(Record("partial-leg", OrderStatus.PartiallyFilled, Now.AddHours(-30), filled: 4));
        store().Save(Record("filled-leg", OrderStatus.Filled, Now.AddHours(-30), filled: 10));
        store().Save(Record("cancelled-leg", OrderStatus.Cancelled, Now.AddHours(-30)));
        store().Save(Record("not-asked", OrderStatus.Accepted, Now.AddHours(-40)));

        var found = store().FindPendingByOrderIds(
            ["partial-leg", "old-leg", "filled-leg", "cancelled-leg", "no-such-order"]);

        found.Select(r => r.OrderId).Should().Equal("old-leg", "partial-leg");
        found[1].FilledQuantity.Should().Be(4);
    }

    // T-10-860: 空集合は何も返さない（全件を返す側へ倒さない）。
    [Theory]
    [MemberData(nameof(Implementations))]
    public void 注文ID指定の抽出は空集合なら空を返す(string kind)
    {
        var store = Stores(kind);
        store().Save(Record("leg", OrderStatus.Accepted, Now.AddDays(-3)));

        store().FindPendingByOrderIds([]).Should().BeEmpty();
    }

    // T-10-861: 非終端の記録の追跡の起点を進め、窓（FindPendingSince）へ戻す。時刻以外は書かない。
    [Theory]
    [MemberData(nameof(Implementations))]
    public void 追跡の起点の付け直しは非終端の記録の時刻だけを進めて窓へ戻す(string kind)
    {
        var store = Stores(kind);
        var leg = Record("leg", OrderStatus.PartiallyFilled, Now.AddHours(-25), filled: 4);
        store().Save(leg);
        store().FindPendingSince(Now.AddHours(-24), batchSize: 10).Should().BeEmpty();

        store().RenewTracking("leg", Now).Should().BeTrue();

        var renewed = store().FindPendingSince(Now.AddHours(-24), batchSize: 10).Should().ContainSingle().Subject;
        renewed.ExecutedAt.Should().Be(Now);
        renewed.Status.Should().Be(OrderStatus.PartiallyFilled);
        renewed.FilledQuantity.Should().Be(4);
        renewed.AveragePrice.Should().Be(949.5m);
        renewed.DecisionId.Should().Be(leg.DecisionId);
    }

    // T-10-861: 終端・不在・巻き戻し（起点を過去へ動かす）は何もしない。
    [Theory]
    [MemberData(nameof(Implementations))]
    public void 追跡の起点の付け直しは終端と不在と巻き戻しでは何もしない(string kind)
    {
        var store = Stores(kind);
        store().Save(Record("filled", OrderStatus.Filled, Now.AddHours(-25), filled: 10));
        store().Save(Record("fresh", OrderStatus.Accepted, Now));

        store().RenewTracking("filled", Now).Should().BeFalse();
        store().RenewTracking("no-such-order", Now).Should().BeFalse();
        store().RenewTracking("fresh", Now.AddHours(-1)).Should().BeFalse();
        store().RenewTracking("fresh", Now).Should().BeFalse();

        var all = store().GetAll().ToDictionary(r => r.OrderId);
        all["filled"].ExecutedAt.Should().Be(Now.AddHours(-25));
        all["filled"].Status.Should().Be(OrderStatus.Filled);
        all["fresh"].ExecutedAt.Should().Be(Now);
    }

    // T-10-861（EF 固有）: 付け直しは時刻の列だけを書く。ガードのコンテキストが非終端の行を読んだ後に、約定追跡の
    // コンテキストが終端を書いても、ガードの付け直しは状態・数量を古い値で上書きしない（別々のスコープで同じ行を書く）。
    [Fact]
    public void EFの付け直しは読んだ後に別のコンテキストが終端を書いても状態と数量を巻き戻さない()
    {
        var dbName = Guid.NewGuid().ToString();
        OrderExecutionDbContext NewContext() => new(
            new DbContextOptionsBuilder<OrderExecutionDbContext>().UseInMemoryDatabase(dbName).Options);

        var leg = Record("leg", OrderStatus.Accepted, Now.AddHours(-25));
        using (var db = NewContext())
            new EfExecutedOrderStore(db).Save(leg);

        using var guardContext = NewContext();
        var guardStore = new EfExecutedOrderStore(guardContext);
        guardStore.FindByDecisionId(leg.DecisionId)!.Status.Should().Be(OrderStatus.Accepted); // 非終端として読んだ（追跡される）

        using (var pollerContext = NewContext())
        {
            new EfExecutedOrderStore(pollerContext)
                .UpdateOutcome("leg", OrderStatus.Filled, 10, 949.5m, 0m, Now.AddSeconds(-5))
                .Should().BeTrue();
        }

        guardStore.RenewTracking("leg", Now);

        using var reader = NewContext();
        var row = new EfExecutedOrderStore(reader).GetAll().Should().ContainSingle().Subject;
        row.Status.Should().Be(OrderStatus.Filled);
        row.FilledQuantity.Should().Be(10);
        row.AveragePrice.Should().Be(949.5m);
    }
}
