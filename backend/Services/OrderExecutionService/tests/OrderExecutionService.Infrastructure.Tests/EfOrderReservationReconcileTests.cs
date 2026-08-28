using OrderExecutionService.Application.Ports;
using OrderExecutionService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace OrderExecutionService.Infrastructure.Tests;

// #141, FR-05, IADR-0074: EF 実装での滞留走査（FindStalledReserved）・解放（Release）のラウンドトリップ。
// Reserved のみ・閾値より古いもののみを対象にし、終端行は決して削除しないことを検証する。
public class EfOrderReservationReconcileTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 6, 0, 0, TimeSpan.Zero);

    private static OrderExecutionDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<OrderExecutionDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    [Fact]
    public void FindStalledReservedは閾値より古いReservedのみを返す()
    {
        var dbName = Guid.NewGuid().ToString();
        var old = Guid.NewGuid();
        var recent = Guid.NewGuid();
        var done = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            var store = new EfOrderReservationStore(db);
            store.TryReserve(old, Now.AddHours(-48));
            store.TryReserve(recent, Now.AddHours(-1));
            store.TryReserve(done, Now.AddHours(-48));
            store.MarkCompleted(done, "BRK-DONE", Now.AddHours(-48));
        }

        using var db2 = NewContext(dbName);
        var stalled = new EfOrderReservationStore(db2).FindStalledReserved(Now.AddHours(-24), batchSize: 50);

        stalled.Select(r => r.DecisionId).Should().Equal(old);
    }

    [Fact]
    public void FindStalledReservedはbatchSizeで打ち切りReservedAt昇順で返す()
    {
        var dbName = Guid.NewGuid().ToString();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        using (var db = NewContext(dbName))
        {
            var store = new EfOrderReservationStore(db);
            store.TryReserve(second, Now.AddHours(-30));
            store.TryReserve(first, Now.AddHours(-48)); // より古い＝先頭
            store.TryReserve(Guid.NewGuid(), Now.AddHours(-25));
        }

        using var db2 = NewContext(dbName);
        var stalled = new EfOrderReservationStore(db2).FindStalledReserved(Now.AddHours(-24), batchSize: 2);

        stalled.Should().HaveCount(2);
        stalled.Select(r => r.DecisionId).Should().Equal(first, second);
    }

    [Fact]
    public void ReleaseはReservedを削除する()
    {
        var dbName = Guid.NewGuid().ToString();
        var id = Guid.NewGuid();
        using (var db = NewContext(dbName))
            new EfOrderReservationStore(db).TryReserve(id, Now.AddHours(-48));

        using (var db = NewContext(dbName))
            new EfOrderReservationStore(db).Release(id).Should().BeTrue();

        using var db2 = NewContext(dbName);
        new EfOrderReservationStore(db2).Find(id).Should().BeNull();
    }

    [Fact]
    public void ReleaseはCompletedを削除しない()
    {
        // 終端行を消せば再配送で二重発注＝実弾では実損。述語で Reserved のみに限定していることを検証する。
        var dbName = Guid.NewGuid().ToString();
        var id = Guid.NewGuid();
        using (var db = NewContext(dbName))
        {
            var store = new EfOrderReservationStore(db);
            store.TryReserve(id, Now.AddHours(-48));
            store.MarkCompleted(id, "BRK-1", Now.AddHours(-48));
        }

        using (var db = NewContext(dbName))
            new EfOrderReservationStore(db).Release(id).Should().BeFalse();

        using var db2 = NewContext(dbName);
        new EfOrderReservationStore(db2).Find(id)!.State.Should().Be(OrderDispatchState.Completed);
    }
}
