using CostControlService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CostControlService.Tests;

// NFR（運用）, #137, IADR-0059: 重複排除ストアの保持期間パージ（EF 実装）を InMemory DB で検証する。
// 検証の主眼は **述語**（cutoff より新しい行に触れないこと）。実 PostgreSQL 上の実行計画・大量行での
// 所要時間・ProcessedAt インデックスの有効性は #82 系の実コンテナ E2E に分離する。
public class EfProcessedMessageStorePurgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 6, 0, 0, TimeSpan.Zero);

    private static DbContextOptions<CostControlDbContext> NewOptions(string dbName) =>
        new DbContextOptionsBuilder<CostControlDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

    [Fact]
    public void パージは_cutoff_より古い行だけを消す()
    {
        var options = NewOptions(Guid.NewGuid().ToString());
        var store = new EfProcessedMessageStore(options);
        var cutoff = Now.AddDays(-90);
        var old = Guid.NewGuid();
        var recent = Guid.NewGuid();

        store.TryMarkProcessed(old, cutoff.AddSeconds(-1));
        store.TryMarkProcessed(recent, cutoff.AddSeconds(1));

        store.PurgeProcessedBefore(cutoff, batchSize: 100).Should().Be(1);

        // 消えた古い行は再び処理できる（重複排除の記憶が消えた）。
        store.TryMarkProcessed(old, Now).Should().BeTrue();
        // 残った新しい行は依然として重複排除が効く（二重計上しない）。
        store.TryMarkProcessed(recent, Now).Should().BeFalse();
    }

    [Fact]
    public void cutoff_ちょうどの行は消さない_境界は古い側のみ()
    {
        var options = NewOptions(Guid.NewGuid().ToString());
        var store = new EfProcessedMessageStore(options);
        var cutoff = Now.AddDays(-90);
        var id = Guid.NewGuid();

        store.TryMarkProcessed(id, cutoff);

        store.PurgeProcessedBefore(cutoff, batchSize: 100).Should().Be(0);
        store.TryMarkProcessed(id, Now).Should().BeFalse();
    }

    [Fact]
    public void パージは_batchSize_を超えて消さない()
    {
        var options = NewOptions(Guid.NewGuid().ToString());
        var store = new EfProcessedMessageStore(options);
        var cutoff = Now.AddDays(-90);
        for (var i = 0; i < 10; i++)
            store.TryMarkProcessed(Guid.NewGuid(), cutoff.AddDays(-1));

        store.PurgeProcessedBefore(cutoff, batchSize: 4).Should().Be(4);
        store.PurgeProcessedBefore(cutoff, batchSize: 4).Should().Be(4);
        store.PurgeProcessedBefore(cutoff, batchSize: 4).Should().Be(2);
        store.PurgeProcessedBefore(cutoff, batchSize: 4).Should().Be(0);
    }

    [Fact]
    public void 対象が無ければ何も消さない()
    {
        var options = NewOptions(Guid.NewGuid().ToString());
        var store = new EfProcessedMessageStore(options);
        store.TryMarkProcessed(Guid.NewGuid(), Now);

        store.PurgeProcessedBefore(Now.AddDays(-90), batchSize: 100).Should().Be(0);
    }

    [Fact]
    public void パージは別コンテキストからも観測できる_永続化されている()
    {
        var options = NewOptions(Guid.NewGuid().ToString());
        var cutoff = Now.AddDays(-90);
        var id = Guid.NewGuid();
        new EfProcessedMessageStore(options).TryMarkProcessed(id, cutoff.AddDays(-1));

        new EfProcessedMessageStore(options).PurgeProcessedBefore(cutoff, batchSize: 100).Should().Be(1);

        using var db = new CostControlDbContext(options);
        db.ProcessedMessages.Any(r => r.MessageId == id).Should().BeFalse();
    }
}
