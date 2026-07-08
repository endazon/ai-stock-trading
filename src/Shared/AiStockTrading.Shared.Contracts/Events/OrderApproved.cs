using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, UC-01, UC-02: リスク管理が注文を承認した（発注執行へ）
public record OrderApproved(
    Guid DecisionId,
    OrderIntent Intent,
    int ApprovedQuantity,
    DateTimeOffset ApprovedAt);
