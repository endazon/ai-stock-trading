using AiStockTrading.Shared.Contracts.Events;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-10, FR-11: スクリーニング結果。承認なら OrderApproved、拒否なら OrderRejected（理由列挙つき）を保持する。
// ホスト（Slice B）はこの結果に応じて対応するイベントをバスへ発行する。
public record ScreeningOutcome
{
    public required bool IsApproved { get; init; }

    /// <summary>承認時の発行イベント。拒否時は null。</summary>
    public OrderApproved? Approved { get; init; }

    /// <summary>拒否時の発行イベント。承認時は null。</summary>
    public OrderRejected? Rejected { get; init; }

    public static ScreeningOutcome Approve(OrderApproved approved) =>
        new() { IsApproved = true, Approved = approved };

    public static ScreeningOutcome Reject(OrderRejected rejected) =>
        new() { IsApproved = false, Rejected = rejected };
}
