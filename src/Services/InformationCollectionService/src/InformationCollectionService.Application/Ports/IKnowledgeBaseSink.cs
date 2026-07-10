using AiStockTrading.InformationCollection.Domain;

namespace AiStockTrading.InformationCollection.Application.Ports;

// FR-01, FR-08: 正規化済み情報の保存先（platform ナレッジベース）。既定は no-op/ログ、実 KB 取り込みは #18 で有効化（IADR-0022）。
public interface IKnowledgeBaseSink
{
    Task SaveAsync(IReadOnlyList<CollectedInformation> items, CancellationToken cancellationToken = default);
}
