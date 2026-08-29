using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Events;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, FR-11: スクリーニング結果。承認なら OrderApproved、拒否なら OrderRejected（理由列挙つき）を保持する。
// ホスト（Slice B）はこの結果に応じて対応するイベントをバスへ発行する。
public record ScreeningOutcome
{
    public required bool IsApproved { get; init; }

    /// <summary>承認時の発行イベント。拒否時は null。</summary>
    public OrderApproved? Approved { get; init; }

    /// <summary>拒否時の発行イベント。承認時は null。</summary>
    public OrderRejected? Rejected { get; init; }

    /// <summary>
    /// FR-20, FR-11, #387, IADR-0148 決定3: この審査 1 回ぶんの観測（段階ゲートの統制違反件数の供給元）。
    /// <para>
    /// <b>承認・拒否のどちらでも必ず伴う</b>（<c>required</c>）。拒否だけを観測すると
    /// 「違反 0 件」を主張する根拠が無くなり、未供給と区別できない。
    /// </para>
    /// </summary>
    public required OrderScreeningObservation Observation { get; init; }

    public static ScreeningOutcome Approve(OrderApproved approved, OrderScreeningObservation observation) =>
        new() { IsApproved = true, Approved = approved, Observation = observation };

    public static ScreeningOutcome Reject(OrderRejected rejected, OrderScreeningObservation observation) =>
        new() { IsApproved = false, Rejected = rejected, Observation = observation };
}
