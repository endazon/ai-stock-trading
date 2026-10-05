using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-04, UC-01, UC-02: 取引判断サービスが売買判断を確定した（判断根拠つき）
//
// NFR-01, NFR-02, #689, IADR-0307: 末尾 2 フィールドは**取引サイクルの起点（cycle provenance）**である。
// 注文チェーンの相関は DecisionId だが、DecisionId は判断サービスが**新規採番**するため、
// 起点イベント（PriceMovementDetected / InformationCollected）とは繋がらない。端点間レイテンシは
// サービスを跨ぐため、起点の素性を**イベントに載せて運ぶ**（載せないと下流で結べない）。
// ［2026-10-06 追記 / #1169・IADR-0490 決定2］定時サイクルの DecisionId は新規採番ではなく、起点の EventId・市場・銘柄から
// **決定的に導く**（再配送で同じ値になり、下流の DecisionId の冪等が重複を止める）。ハッシュであり起点へ逆にはたどれないので、
// 起点の素性を載せて運ぶ理由は変わらない。価格変動の判断は従来どおり新規採番。
//   - CycleTrigger: `BusinessMetrics.TriggerScheduled` / `TriggerPriceMovement` の語彙。
//   - CycleStartedAt: 起点イベント自身の時刻（InformationCollected.CollectedAt / PriceMovementDetected.DetectedAt）。
// 🔴 **既定は null（＝起点不明）であり、0 や現在時刻へ倒さない。** 起点を持たない経路（owner 手仕舞い・
// 自動縮小）は実在し、そこで 0 を作ると「即座に完了した」と読めてしまう（未観測は未観測として出す）。
//
// FR-10, FR-11, ADR-0049 決定3, #1120, IADR-0465 決定2: StopWidth は**新規建ての損切り幅に下限を掛けた結果**
// （AI の幅・下限・出所・適用した幅・広げたか）。監査台帳へ残すために判断の記録へ載せる（IADR-0460 決定1 を改めた）。
// 🔴 **既定は null**。損切りラインを作らない判断（決済・owner 手仕舞い・自動縮小）は持たない。0 で埋めない。
public record TradeDecisionMade(
    Guid DecisionId,
    OrderIntent Intent,
    string Rationale,
    DateTimeOffset DecidedAt,
    string? CycleTrigger = null,
    DateTimeOffset? CycleStartedAt = null,
    StopWidthFloorApplication? StopWidth = null);
