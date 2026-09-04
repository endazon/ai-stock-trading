using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-04, UC-01, UC-02: 取引判断サービスが売買判断を確定した（判断根拠つき）
//
// NFR-01, NFR-02, #689, IADR-0307: 末尾 2 フィールドは**取引サイクルの起点（cycle provenance）**である。
// 注文チェーンの相関は DecisionId だが、DecisionId は判断サービスが**新規採番**するため、
// 起点イベント（PriceMovementDetected / InformationCollected）とは繋がらない。端点間レイテンシは
// サービスを跨ぐため、起点の素性を**イベントに載せて運ぶ**（載せないと下流で結べない）。
//   - CycleTrigger: `BusinessMetrics.TriggerScheduled` / `TriggerPriceMovement` の語彙。
//   - CycleStartedAt: 起点イベント自身の時刻（InformationCollected.CollectedAt / PriceMovementDetected.DetectedAt）。
// 🔴 **既定は null（＝起点不明）であり、0 や現在時刻へ倒さない。** 起点を持たない経路（owner 手仕舞い・
// 自動縮小）は実在し、そこで 0 を作ると「即座に完了した」と読めてしまう（未観測は未観測として出す）。
public record TradeDecisionMade(
    Guid DecisionId,
    OrderIntent Intent,
    string Rationale,
    DateTimeOffset DecidedAt,
    string? CycleTrigger = null,
    DateTimeOffset? CycleStartedAt = null);
