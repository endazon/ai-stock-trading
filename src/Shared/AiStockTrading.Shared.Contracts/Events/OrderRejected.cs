using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, FR-11, FR-19: リスク管理が注文を拒否した（理由列挙つき。監査・通知用）
public record OrderRejected(
    Guid DecisionId,
    OrderIntent Intent,
    IReadOnlyList<RejectionReason> Reasons,
    DateTimeOffset RejectedAt);
