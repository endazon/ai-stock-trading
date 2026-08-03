using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Domain;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.InformationCollection.Infrastructure.Composable.Adapters;

// FR-01, FR-08, IADR-0022: 既定の安全 KB シンク。実 platform KB に保存せずログに出力するだけ（no-op）。
// 実 KB 取り込み（FR-08・#18）は構成有効化で差し替える。
internal sealed class LoggingKnowledgeBaseSink(ILogger<LoggingKnowledgeBaseSink> logger) : IKnowledgeBaseSink
{
    public Task SaveAsync(IReadOnlyList<CollectedInformation> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        logger.LogInformation("KB 保存（no-op）: {Count} 件の正規化済み情報（実 KB 連携は #18）。", items.Count);
        return Task.CompletedTask;
    }
}
