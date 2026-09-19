using AiStockTrading.Shared.Contracts.Events;

namespace RiskManagementService.Features.RiskManagement.CancelPositionClose;

// FR-05, FR-10, FR-11, UC-06, ADR-0003, #847, #768, IADR-0357:
// 利用者（owner）による「板に残った手仕舞いの取消」要求と、その判定結果。

/// <summary>
/// 取消要求。対象は<b>手仕舞い（<c>PositionEffect.Close</c>）の承認</b>だけであり、
/// <c>DecisionId</c> で指す（利用者は手仕舞いの応答でこの値を受け取っている）。理由は必須。
/// </summary>
public sealed record PositionCloseCancellationCommand(Guid DecisionId, string Reason);

/// <summary>取消要求を受理できない理由。<see cref="PositionCloseCancellationRejection.None"/> が受理。</summary>
public enum PositionCloseCancellationRejection
{
    None = 0,

    /// <summary>台帳に当該 <c>DecisionId</c> の承認が無い（未知の注文は取り消せない）。</summary>
    OrderNotFound,

    /// <summary>
    /// 手仕舞い以外の注文（エントリー等）。本エンドポイントの役目ではない
    /// ——保護逆指値が張れないエントリーの取消は発注執行が自ら行う（IADR-0210）。
    /// </summary>
    NotACloseOrder,
}

/// <summary>
/// 取消要求の判定結果。受理時は監査イベント（<see cref="PositionCloseCancellationRequested"/>）を持つ。
/// <b>発行は呼び出し側（エンドポイント）</b>であり、<see cref="PositionCloseOutcome"/> と同じ形に揃えてある。
/// </summary>
public sealed record PositionCloseCancellationOutcome(
    PositionCloseCancellationRejection Rejection,
    PositionCloseCancellationRequested? Requested = null)
{
    public bool Accepted => Rejection == PositionCloseCancellationRejection.None;

    public static PositionCloseCancellationOutcome Reject(PositionCloseCancellationRejection rejection) =>
        new(rejection);
}
