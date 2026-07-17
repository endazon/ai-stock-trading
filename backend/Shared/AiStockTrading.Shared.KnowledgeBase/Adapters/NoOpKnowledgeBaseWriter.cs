using AiStockTrading.Shared.KnowledgeBase.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.KnowledgeBase.Adapters;

// FR-08, IADR-0069 決定 2: 既定の安全な保存シンク。実 platform KB に保存せずログに出すだけ（未保存を返す）。
// KnowledgeBase:Documents:BaseUrl 未設定/不正のとき選択される（現行挙動＝保存しないを保持する）。
internal sealed class NoOpKnowledgeBaseWriter(ILogger<NoOpKnowledgeBaseWriter> logger) : IKnowledgeBaseWriter
{
    public Task<KnowledgeWriteResult> SaveAsync(KnowledgeDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        logger.LogInformation("KB 保存（no-op）: 「{Title}」（実 platform 保存は KnowledgeBase:Documents:BaseUrl 設定で opt-in・#18）。", document.Title);
        return Task.FromResult(KnowledgeWriteResult.NotSaved);
    }
}
