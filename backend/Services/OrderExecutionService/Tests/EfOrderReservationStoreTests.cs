using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace OrderExecutionService.Tests;

// #131, FR-05, IADR-0057: 発注前 DecisionId 予約の永続化を InMemory DB で検証する。
// 実 PostgreSQL の一意制約による並行予約の排他は実基盤 E2E 側で担保する（本テストは契約とラウンドトリップ）。
public class EfOrderReservationStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 6, 0, 0, TimeSpan.Zero);

    private static OrderExecutionDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<OrderExecutionDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    [Fact]
    public void 予約はラウンドトリップし別コンテキストからも読める()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            new EfOrderReservationStore(db).TryReserve(decisionId, Now).Should().BeTrue();
        }

        using var db2 = NewContext(dbName);
        var reservation = new EfOrderReservationStore(db2).Find(decisionId);
        reservation.Should().NotBeNull();
        reservation!.State.Should().Be(OrderDispatchState.Reserved);
        reservation.ReservedAt.Should().Be(Now);
        reservation.BrokerOrderId.Should().BeNull();
    }

    [Fact]
    public void 同一DecisionIdの再予約はfalseを返す()
    {
        // 再配送・並行配送で同じ OrderApproved を掴んでも、予約を確保できるのは高々1つ（＝発注も高々1回）。
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();
        using var db = NewContext(dbName);
        var store = new EfOrderReservationStore(db);

        store.TryReserve(decisionId, Now).Should().BeTrue();
        store.TryReserve(decisionId, Now.AddSeconds(2)).Should().BeFalse();
    }

    [Fact]
    public void 別プロセスが確保済みの予約は別コンテキストからもfalseになる()
    {
        // 予約はブローカ発注の前にコミットされるため、別コンテキスト（別プロセス相当）から観測できる。
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            new EfOrderReservationStore(db).TryReserve(decisionId, Now).Should().BeTrue();
        }

        using var db2 = NewContext(dbName);
        new EfOrderReservationStore(db2).TryReserve(decisionId, Now).Should().BeFalse();
    }

    [Fact]
    public void 確定するとCompletedとブローカ注文IDが記録される()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            var store = new EfOrderReservationStore(db);
            store.TryReserve(decisionId, Now);
            store.MarkCompleted(decisionId, "o1", Now.AddSeconds(1));
        }

        using var db2 = NewContext(dbName);
        var reservation = new EfOrderReservationStore(db2).Find(decisionId);
        reservation!.State.Should().Be(OrderDispatchState.Completed);
        reservation.BrokerOrderId.Should().Be("o1");
    }

    [Fact]
    public void 未予約のDecisionIdはnullを返す()
    {
        using var db = NewContext(Guid.NewGuid().ToString());

        new EfOrderReservationStore(db).Find(Guid.NewGuid()).Should().BeNull();
    }
}
