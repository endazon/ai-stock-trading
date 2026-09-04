using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, UC-01, UC-02: リスク管理が注文を承認した（発注執行へ）
// NFR-01, NFR-02, #689, IADR-0307: 末尾 2 フィールドは取引サイクルの起点（cycle provenance）を
// 判断から発注執行へ中継するものである（意味と null の扱いは TradeDecisionMade を参照）。
// 判断を経ない承認（owner 手仕舞い・維持証拠金の自動縮小）は null のままにする——
// **取引サイクルではないものにサイクルの起点を作らない。**
public record OrderApproved(
    Guid DecisionId,
    OrderIntent Intent,
    int ApprovedQuantity,
    DateTimeOffset ApprovedAt,
    string? CycleTrigger = null,
    DateTimeOffset? CycleStartedAt = null);
