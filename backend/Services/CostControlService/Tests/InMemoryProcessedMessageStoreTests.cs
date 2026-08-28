using CostControlService.Infrastructure.Persistence;
using AwesomeAssertions;
using Xunit;

namespace CostControlService.Tests;

// NFR（費用）, IADR-0055 決定5: 重複排除ストアの基本性質（初回のみ true・Unmark で再試行可能）。
public class InMemoryProcessedMessageStoreTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    [Fact]
    public void 初回は_true_二回目以降は_false_を返す()
    {
        var store = new InMemoryProcessedMessageStore();
        var id = Guid.NewGuid();

        store.TryMarkProcessed(id, At).Should().BeTrue();
        store.TryMarkProcessed(id, At).Should().BeFalse();
        store.TryMarkProcessed(id, At).Should().BeFalse();
    }

    [Fact]
    public void 別の_MessageId_は互いに影響しない()
    {
        var store = new InMemoryProcessedMessageStore();

        store.TryMarkProcessed(Guid.NewGuid(), At).Should().BeTrue();
        store.TryMarkProcessed(Guid.NewGuid(), At).Should().BeTrue();
    }

    [Fact]
    public void Unmark_すると再び処理できる_計上失敗時の再試行()
    {
        var store = new InMemoryProcessedMessageStore();
        var id = Guid.NewGuid();

        store.TryMarkProcessed(id, At).Should().BeTrue();
        store.Unmark(id);

        store.TryMarkProcessed(id, At).Should().BeTrue();
    }

    [Fact]
    public void 未記録の_Unmark_は無害()
    {
        var store = new InMemoryProcessedMessageStore();

        var act = () => store.Unmark(Guid.NewGuid());

        act.Should().NotThrow();
    }

    // NFR（運用）, #137, IADR-0059: 保持期間ベースのパージ。
    // 消し過ぎ＝重複排除の素通り＝費用の二重計上であり、「cutoff より新しい行を残す」ことが本質。

    [Fact]
    public void パージは_cutoff_より古い行だけを消す()
    {
        var store = new InMemoryProcessedMessageStore();
        var old = Guid.NewGuid();
        var recent = Guid.NewGuid();
        var cutoff = At.AddDays(90);

        store.TryMarkProcessed(old, cutoff.AddSeconds(-1));
        store.TryMarkProcessed(recent, cutoff.AddSeconds(1));

        store.PurgeProcessedBefore(cutoff, batchSize: 100).Should().Be(1);

        // 消えた古い行は再度処理できる（＝重複排除の記憶が消えた）。
        store.TryMarkProcessed(old, At).Should().BeTrue();
        // 残った新しい行は依然として重複排除が効く（＝二重計上しない）。
        store.TryMarkProcessed(recent, At).Should().BeFalse();
    }

    [Fact]
    public void cutoff_ちょうどの行は消さない_境界は古い側のみ()
    {
        var store = new InMemoryProcessedMessageStore();
        var id = Guid.NewGuid();
        var cutoff = At.AddDays(90);

        store.TryMarkProcessed(id, cutoff);

        store.PurgeProcessedBefore(cutoff, batchSize: 100).Should().Be(0);
        store.TryMarkProcessed(id, At).Should().BeFalse();
    }

    [Fact]
    public void パージは_batchSize_を超えて消さない()
    {
        var store = new InMemoryProcessedMessageStore();
        var cutoff = At.AddDays(90);
        for (var i = 0; i < 10; i++)
            store.TryMarkProcessed(Guid.NewGuid(), At);

        store.PurgeProcessedBefore(cutoff, batchSize: 4).Should().Be(4);
        store.PurgeProcessedBefore(cutoff, batchSize: 4).Should().Be(4);
        store.PurgeProcessedBefore(cutoff, batchSize: 4).Should().Be(2);
        store.PurgeProcessedBefore(cutoff, batchSize: 4).Should().Be(0);
    }

    [Fact]
    public void 対象が無ければ何も消さない()
    {
        var store = new InMemoryProcessedMessageStore();
        store.TryMarkProcessed(Guid.NewGuid(), At.AddDays(100));

        store.PurgeProcessedBefore(At.AddDays(90), batchSize: 100).Should().Be(0);
    }
}
