using AiStockTrading.InformationCollection.Domain;

namespace AiStockTrading.InformationCollection.Application.State;

// FR-01: 1 巡回の収集結果。ItemCount は許可リスト選別・正規化・KB 保存まで完了した件数。
public sealed record CollectionResult(int ItemCount, IReadOnlyList<CollectedInformation> Items);
