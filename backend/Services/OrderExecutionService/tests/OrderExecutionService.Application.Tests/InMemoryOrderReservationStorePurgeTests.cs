using AiStockTrading.OrderExecution.Application.Adapters;
using AiStockTrading.OrderExecution.Application.Ports;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.OrderExecution.Application.Tests;

// NFR（運用）, #137, FR-05, IADR-0059: 発注予約の保持期間パージ。
//
// 本テスト群の中心は「Reserved は期限を過ぎても消さない」ことである。Reserved＝「ブローカへ発注済みか
// 不明」であり、消せば再配送で二重発注＝実弾では実損になる（IADR-0057 が防いでいるものをパージが壊す）。
// 滞留 Reserved は時間経過で安全になる類の行ではなく、#141（自動リコンサイル）か人手が解消する領域。
public class InMemoryOrderReservationStorePurgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reserved_は期限を遥かに過ぎていてもパージしない()
    {
        var store = new InMemoryOrderReservationStore();
        var decisionId = Guid.NewGuid();
        // 10 年前に予約したきり確定していない＝発注済みか不明のまま滞留している行。
        store.TryReserve(decisionId, Now.AddYears(-10));

        store.PurgeCompletedBefore(Now, batchSize: 100).Should().Be(0);

        // 予約は残り続け、再配送は依然として弾かれる（二重発注しない）。
        store.Find(decisionId)!.State.Should().Be(OrderDispatchState.Reserved);
        store.TryReserve(decisionId, Now).Should().BeFalse();
    }

    [Fact]
    public void Completed_かつ_cutoff_より古い予約だけをパージする()
    {
        var store = new InMemoryOrderReservationStore();
        var oldCompleted = Guid.NewGuid();
        var recentCompleted = Guid.NewGuid();
        var cutoff = Now.AddDays(-90);

        store.TryReserve(oldCompleted, cutoff.AddDays(-10));
        store.MarkCompleted(oldCompleted, "o-old", cutoff.AddSeconds(-1));

        store.TryReserve(recentCompleted, cutoff.AddDays(-10));
        store.MarkCompleted(recentCompleted, "o-recent", cutoff.AddSeconds(1));

        store.PurgeCompletedBefore(cutoff, batchSize: 100).Should().Be(1);

        store.Find(oldCompleted).Should().BeNull();
        store.Find(recentCompleted).Should().NotBeNull();
    }

    [Fact]
    public void 判定は_ReservedAt_ではなく_CompletedAt_で行う()
    {
        var store = new InMemoryOrderReservationStore();
        var decisionId = Guid.NewGuid();
        var cutoff = Now.AddDays(-90);

        // 予約は古いが、確定したのは cutoff より後（＝まだ再配送猶予の内側）。消してはならない。
        store.TryReserve(decisionId, cutoff.AddDays(-30));
        store.MarkCompleted(decisionId, "o1", cutoff.AddDays(1));

        store.PurgeCompletedBefore(cutoff, batchSize: 100).Should().Be(0);
        store.Find(decisionId).Should().NotBeNull();
    }

    [Fact]
    public void cutoff_ちょうどの_Completed_は消さない_境界は古い側のみ()
    {
        var store = new InMemoryOrderReservationStore();
        var decisionId = Guid.NewGuid();
        var cutoff = Now.AddDays(-90);

        store.TryReserve(decisionId, cutoff.AddDays(-1));
        store.MarkCompleted(decisionId, "o1", cutoff);

        store.PurgeCompletedBefore(cutoff, batchSize: 100).Should().Be(0);
        store.Find(decisionId).Should().NotBeNull();
    }

    [Fact]
    public void MarkCompleted_は確定時刻を保持する()
    {
        // パージの述語が CompletedAt に依るため、確定時刻が失われていないことが前提条件になる。
        var store = new InMemoryOrderReservationStore();
        var decisionId = Guid.NewGuid();
        var completedAt = Now.AddMinutes(5);

        store.TryReserve(decisionId, Now);
        store.MarkCompleted(decisionId, "o1", completedAt);

        store.Find(decisionId)!.CompletedAt.Should().Be(completedAt);
    }

    [Fact]
    public void パージは_batchSize_を超えて消さない()
    {
        var store = new InMemoryOrderReservationStore();
        var cutoff = Now.AddDays(-90);
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            store.TryReserve(id, cutoff.AddDays(-10));
            store.MarkCompleted(id, $"o{i}", cutoff.AddDays(-5));
        }

        store.PurgeCompletedBefore(cutoff, batchSize: 2).Should().Be(2);
        store.PurgeCompletedBefore(cutoff, batchSize: 2).Should().Be(2);
        store.PurgeCompletedBefore(cutoff, batchSize: 2).Should().Be(1);
        store.PurgeCompletedBefore(cutoff, batchSize: 2).Should().Be(0);
    }

    [Fact]
    public void Reserved_と_Completed_が混在しても_Reserved_だけが残る()
    {
        var store = new InMemoryOrderReservationStore();
        var stuck = Guid.NewGuid();
        var done = Guid.NewGuid();
        var cutoff = Now.AddDays(-90);

        store.TryReserve(stuck, cutoff.AddDays(-100));
        store.TryReserve(done, cutoff.AddDays(-100));
        store.MarkCompleted(done, "o1", cutoff.AddDays(-99));

        store.PurgeCompletedBefore(cutoff, batchSize: 100).Should().Be(1);

        store.Find(stuck)!.State.Should().Be(OrderDispatchState.Reserved);
        store.Find(done).Should().BeNull();
    }
}
