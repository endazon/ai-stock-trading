using OrderExecutionService.Application.Ports;
using OrderExecutionService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace OrderExecutionService.Infrastructure.Tests;

// NFR（運用）, #137, FR-05, IADR-0059: 発注予約の保持期間パージ（EF 実装）を InMemory DB で検証する。
// 検証の主眼は **述語**（どの行を消し、どの行を絶対に消さないか）である。実 PostgreSQL 上の実行計画・
// 大量行での所要時間・インデックス有効性は #82 系の実コンテナ E2E に分離する（本 PR のスコープ外）。
public class EfOrderReservationStorePurgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 6, 0, 0, TimeSpan.Zero);

    private static OrderExecutionDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<OrderExecutionDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    [Fact]
    public void Reserved_は期限を遥かに過ぎていてもパージしない()
    {
        // 本 PR で最も重要な回帰テスト。Reserved＝「ブローカへ発注済みか不明」であり、消せば再配送で
        // 二重発注＝実弾では実損（IADR-0057 が防ぐものをパージが破壊する）。
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            new EfOrderReservationStore(db).TryReserve(decisionId, Now.AddYears(-10));
        }

        using (var db = NewContext(dbName))
        {
            new EfOrderReservationStore(db).PurgeCompletedBefore(Now, batchSize: 100).Should().Be(0);
        }

        using (var db = NewContext(dbName))
        {
            var store = new EfOrderReservationStore(db);
            store.Find(decisionId)!.State.Should().Be(OrderDispatchState.Reserved);
            // 予約が残っている＝再配送は依然として弾かれる。
            store.TryReserve(decisionId, Now).Should().BeFalse();
        }
    }

    [Fact]
    public void Completed_かつ_cutoff_より古い予約だけをパージする()
    {
        var dbName = Guid.NewGuid().ToString();
        var cutoff = Now.AddDays(-90);
        var oldCompleted = Guid.NewGuid();
        var recentCompleted = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            var store = new EfOrderReservationStore(db);
            store.TryReserve(oldCompleted, cutoff.AddDays(-10));
            store.MarkCompleted(oldCompleted, "o-old", cutoff.AddSeconds(-1));
            store.TryReserve(recentCompleted, cutoff.AddDays(-10));
            store.MarkCompleted(recentCompleted, "o-recent", cutoff.AddSeconds(1));
        }

        using (var db = NewContext(dbName))
        {
            new EfOrderReservationStore(db).PurgeCompletedBefore(cutoff, batchSize: 100).Should().Be(1);
        }

        using (var db = NewContext(dbName))
        {
            var store = new EfOrderReservationStore(db);
            store.Find(oldCompleted).Should().BeNull();
            store.Find(recentCompleted).Should().NotBeNull();
        }
    }

    [Fact]
    public void 判定は_ReservedAt_ではなく_CompletedAt_で行う()
    {
        var dbName = Guid.NewGuid().ToString();
        var cutoff = Now.AddDays(-90);
        var decisionId = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            var store = new EfOrderReservationStore(db);
            // 予約は古いが確定は cutoff より後＝まだ再配送猶予の内側。消してはならない。
            store.TryReserve(decisionId, cutoff.AddDays(-30));
            store.MarkCompleted(decisionId, "o1", cutoff.AddDays(1));
        }

        using (var db = NewContext(dbName))
        {
            new EfOrderReservationStore(db).PurgeCompletedBefore(cutoff, batchSize: 100).Should().Be(0);
        }

        using var verify = NewContext(dbName);
        new EfOrderReservationStore(verify).Find(decisionId).Should().NotBeNull();
    }

    [Fact]
    public void cutoff_ちょうどの_Completed_は消さない_境界は古い側のみ()
    {
        var dbName = Guid.NewGuid().ToString();
        var cutoff = Now.AddDays(-90);
        var decisionId = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            var store = new EfOrderReservationStore(db);
            store.TryReserve(decisionId, cutoff.AddDays(-1));
            store.MarkCompleted(decisionId, "o1", cutoff);
        }

        using (var db = NewContext(dbName))
        {
            new EfOrderReservationStore(db).PurgeCompletedBefore(cutoff, batchSize: 100).Should().Be(0);
        }

        using var verify = NewContext(dbName);
        new EfOrderReservationStore(verify).Find(decisionId).Should().NotBeNull();
    }

    [Fact]
    public void パージは_batchSize_を超えて消さない()
    {
        var dbName = Guid.NewGuid().ToString();
        var cutoff = Now.AddDays(-90);

        using (var db = NewContext(dbName))
        {
            var store = new EfOrderReservationStore(db);
            for (var i = 0; i < 5; i++)
            {
                var id = Guid.NewGuid();
                store.TryReserve(id, cutoff.AddDays(-10));
                store.MarkCompleted(id, $"o{i}", cutoff.AddDays(-5));
            }
        }

        using (var db = NewContext(dbName))
        {
            var store = new EfOrderReservationStore(db);
            store.PurgeCompletedBefore(cutoff, batchSize: 2).Should().Be(2);
            store.PurgeCompletedBefore(cutoff, batchSize: 2).Should().Be(2);
            store.PurgeCompletedBefore(cutoff, batchSize: 2).Should().Be(1);
            store.PurgeCompletedBefore(cutoff, batchSize: 2).Should().Be(0);
        }
    }

    [Fact]
    public void 確定時刻は永続化され_Find_で読める()
    {
        // パージの述語が CompletedAt に依るため、確定時刻の永続化が前提条件になる。
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();
        var completedAt = Now.AddMinutes(5);

        using (var db = NewContext(dbName))
        {
            var store = new EfOrderReservationStore(db);
            store.TryReserve(decisionId, Now);
            store.MarkCompleted(decisionId, "o1", completedAt);
        }

        using var verify = NewContext(dbName);
        new EfOrderReservationStore(verify).Find(decisionId)!.CompletedAt.Should().Be(completedAt);
    }
}
