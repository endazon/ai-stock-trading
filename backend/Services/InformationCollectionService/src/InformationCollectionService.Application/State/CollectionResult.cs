using InformationCollectionService.Domain;

namespace InformationCollectionService.Application.State;

// FR-01: 1 巡回の収集結果。ItemCount は許可リスト選別・正規化・KB 保存まで完了した件数。
// ADR-0020 決定3: Degradation は当該巡回の欠測判定（サイクル中止・限定縮退・記録のみ）。
public sealed record CollectionResult(
    int ItemCount,
    IReadOnlyList<CollectedInformation> Items,
    CollectionDegradation Degradation)
{
    public CollectionResult(int itemCount, IReadOnlyList<CollectedInformation> items)
        : this(itemCount, items, CollectionDegradation.None)
    {
    }
}
