using System.Collections.Concurrent;
using AiStockTrading.CostControl.Application.Ports;

namespace AiStockTrading.CostControl.Application.Adapters;

// NFR（費用）, IADR-0055 決定5: 重複排除ストアのインメモリ実装（テスト・単体実行用）。
// PostgreSQL 永続化は Worker の EfProcessedMessageStore で差し替える。TryAdd の原子性で
// 同一 MessageId の同時到達でも高々 1 回だけ true を返す。
public sealed class InMemoryProcessedMessageStore : IProcessedMessageStore
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _processed = new();

    public bool TryMarkProcessed(Guid messageId, DateTimeOffset at) => _processed.TryAdd(messageId, at);

    public void Unmark(Guid messageId) => _processed.TryRemove(messageId, out _);
}
