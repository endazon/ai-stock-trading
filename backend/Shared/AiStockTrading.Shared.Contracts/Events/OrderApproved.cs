using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, UC-01, UC-02: リスク管理が注文を承認した（発注執行へ）
// NFR-01, NFR-02, #689, IADR-0307: 末尾 2 フィールドは取引サイクルの起点（cycle provenance）を
// 判断から発注執行へ中継するものである（意味と null の扱いは TradeDecisionMade を参照）。
// 判断を経ない承認（owner 手仕舞い・維持証拠金の自動縮小）は null のままにする——
// **取引サイクルではないものにサイクルの起点を作らない。**
//
// FR-10, FR-12, ADR-0040 決定1・決定3, #819, IADR-0342 決定3: StopLossMethod は**承認時点で有効だった
// 損切りの実行機構**である。発注執行は承認が運ぶ値で保護レグを扱う（走行中の設定変更と承認の競合を避ける）。
// **任意項目・既定 S0**——本項目を持たない旧いメッセージは S0（序数 0）として読まれ、従来と同一に動く。
// 手法は Open（新規建て）にしか効かないため、Close の承認（owner 手仕舞い・自動縮小）は既定のままにする。
public record OrderApproved(
    Guid DecisionId,
    OrderIntent Intent,
    int ApprovedQuantity,
    DateTimeOffset ApprovedAt,
    string? CycleTrigger = null,
    DateTimeOffset? CycleStartedAt = null,
    StopLossExecutionMethod StopLossMethod = StopLossExecutionMethod.BrokerStopOrder);
