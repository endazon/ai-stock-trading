using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace OrderExecutionService.Tests;

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

    [Fact]
    public void 予約は送る先の取引環境を保存し_滞留の走査と照会で返す_列を足す前の行は不明のまま()
    {
        // T-10-1611, NFR-09, ADR-0045 決定2, #1051, IADR-0444 決定1: リコンサイラは滞留の走査（FindStalledReserved）が返す
        // 取引環境で解放の門を選ぶ。保存・走査・照会（Find）の 3 経路で落ちないこと、取引環境を持たない行（列を足す前の
        // 予約＝null）が SIMULATE へ化けずに null のまま返ることを固定する。
        var dbName = Guid.NewGuid().ToString();
        var simulate = Guid.NewGuid();
        var real = Guid.NewGuid();
        var legacy = Guid.NewGuid();
        using (var db = NewContext(dbName))
        {
            var store = new EfOrderReservationStore(db);
            store.TryReserve(simulate, Now.AddHours(-48), AiStockTrading.Shared.Contracts.Trading.BrokerProvider.MoomooSimulate);
            store.TryReserve(real, Now.AddHours(-47), AiStockTrading.Shared.Contracts.Trading.BrokerProvider.MoomooReal);
            // 列を足す前の行と同じ形（取引環境を持たない）を直接書く。
            db.DispatchReservations.Add(new OrderDispatchReservationRow
            {
                DecisionId = legacy,
                State = OrderDispatchState.Reserved,
                ReservedAt = Now.AddHours(-46),
            });
            db.SaveChanges();
        }

        using var db2 = NewContext(dbName);
        var store2 = new EfOrderReservationStore(db2);
        store2.FindStalledReserved(Now.AddHours(-24), batchSize: 50)
            .Select(r => (r.DecisionId, r.BrokerProvider))
            .Should().Equal(
                (simulate, AiStockTrading.Shared.Contracts.Trading.BrokerProvider.MoomooSimulate),
                (real, AiStockTrading.Shared.Contracts.Trading.BrokerProvider.MoomooReal),
                (legacy, (AiStockTrading.Shared.Contracts.Trading.BrokerProvider?)null));
        store2.Find(real)!.BrokerProvider.Should().Be(AiStockTrading.Shared.Contracts.Trading.BrokerProvider.MoomooReal);
        store2.Find(legacy)!.BrokerProvider.Should().BeNull("不明は不明のまま（SIMULATE と推測しない）");
    }
}
