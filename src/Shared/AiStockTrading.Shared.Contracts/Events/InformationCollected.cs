namespace AiStockTrading.Shared.Contracts.Events;

// FR-01, FR-02, UC-01, IADR-0022: 情報収集サービスが 1 巡回の収集を完了した。取引サイクル（FR-02）の起点となる。
// ItemCount は正規化・KB 保存まで完了したアイテム数。
public record InformationCollected(
    Guid EventId,
    int ItemCount,
    DateTimeOffset CollectedAt);
