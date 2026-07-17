using AiStockTrading.Shared.KnowledgeBase.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.KnowledgeBase.Adapters;

// FR-08, IADR-0069 決定 2: 既定の安全な取得実装。実 platform 検索を呼ばず空を返す（RAG 文脈なしへ縮退）。
// KnowledgeBase:Search:BaseUrl 未設定/不正のとき選択される。
internal sealed class NoOpKnowledgeBaseSearch(ILogger<NoOpKnowledgeBaseSearch> logger) : IKnowledgeBaseSearch
{
    private static readonly IReadOnlyList<KnowledgeHit> Empty = [];

    public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(KnowledgeQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        logger.LogDebug("KB 検索（no-op）: 空を返す（実 platform 検索は KnowledgeBase:Search:BaseUrl 設定で opt-in・#18）。");
        return Task.FromResult(Empty);
    }
}
