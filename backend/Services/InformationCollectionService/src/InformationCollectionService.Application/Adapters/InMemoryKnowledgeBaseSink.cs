using System.Collections.Concurrent;
using InformationCollectionService.Application.Ports;
using InformationCollectionService.Domain;

namespace InformationCollectionService.Application.Adapters;

// FR-01, FR-08: KB シンクのインメモリ実装（テスト・単体実行用）。実 platform KB 取り込みは #18 で差し替える。
public sealed class InMemoryKnowledgeBaseSink : IKnowledgeBaseSink
{
    private readonly ConcurrentQueue<CollectedInformation> _saved = new();

    public IReadOnlyCollection<CollectedInformation> Saved => _saved;

    public Task SaveAsync(IReadOnlyList<CollectedInformation> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var item in items)
            _saved.Enqueue(item);
        return Task.CompletedTask;
    }
}
