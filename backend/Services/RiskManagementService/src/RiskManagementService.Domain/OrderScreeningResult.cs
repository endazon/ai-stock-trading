using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-10, FR-11: 判定結果。拒否時は違反理由を網羅的に列挙する（監査性優先）
public record OrderScreeningResult
{
    public required bool IsApproved { get; init; }

    public int ApprovedQuantity { get; init; }

    public IReadOnlyList<RejectionReason> Reasons { get; init; } = [];

    public static OrderScreeningResult Approve(int approvedQuantity) =>
        new() { IsApproved = true, ApprovedQuantity = approvedQuantity };

    public static OrderScreeningResult Reject(IReadOnlyList<RejectionReason> reasons) =>
        new() { IsApproved = false, Reasons = reasons };
}
