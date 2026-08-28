using System.Collections.Concurrent;
using CostControlService.Application.Ports;

namespace CostControlService.Application.Adapters;

// NFR（費用）, IADR-0055 決定5: 重複排除ストアのインメモリ実装（テスト・単体実行用）。
// PostgreSQL 永続化は Worker の EfProcessedMessageStore で差し替える。TryAdd の原子性で
// 同一 MessageId の同時到達でも高々 1 回だけ true を返す。
public sealed class InMemoryProcessedMessageStore : IProcessedMessageStore
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _processed = new();

    public bool TryMarkProcessed(Guid messageId, DateTimeOffset at) => _processed.TryAdd(messageId, at);

    public void Unmark(Guid messageId) => _processed.TryRemove(messageId, out _);

    // NFR（運用）, #137, IADR-0059: cutoff より古い行のみをバッチ削除する。境界ちょうどは残す（消し過ぎない側）。
    public int PurgeProcessedBefore(DateTimeOffset cutoff, int batchSize)
    {
        var expired = _processed
            .Where(kv => kv.Value < cutoff)
            .Take(batchSize)
            .Select(kv => kv.Key)
            .ToList();

        var purged = 0;
        foreach (var messageId in expired)
        {
            // 走査と削除の間に Unmark 済みなら false。実際に消せた件数だけを数える。
            if (_processed.TryRemove(messageId, out _))
                purged++;
        }

        return purged;
    }
}
