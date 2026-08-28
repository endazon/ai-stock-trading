using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, UC-02, #331, IADR-0210 決定3/4: 保護逆指値が成立しない状態を検知し、
// **逆指値なしの建玉を持たない**ための対処を行った（業務フロー 02「逆指値が成立しない場合の扱い」）。
//
// - Cause=RejectedAtEntry: エントリーと同時の逆指値が未受理だった（未約定なら取消・約定済みなら成行手仕舞い）。
// - Cause=LapsedInFlight: 滞留中の逆指値が失効した（再発注不可→成行手仕舞い）。
// - Remediation=None は**建玉解消も失敗した**状態であり、Critical 通知で人手対応を求める。
//
// Remediation=PositionClosed のとき CloseDecisionId / CloseIntent を持ち、リスク管理が台帳の承認行へ
// 結線する（手仕舞いレグの約定は OrderExecuted 相関で台帳の建玉を減らす。ProtectiveStopPlaced と同じ作法）。
public record ProtectiveStopCoverageLost(
    Guid EntryDecisionId,
    string Symbol,
    Market Market,
    ProtectiveStopLossCause Cause,
    ProtectiveStopRemediation Remediation,
    int Quantity,
    Guid? CloseDecisionId,
    OrderIntent? CloseIntent,
    DateTimeOffset OccurredAt);

// #331, IADR-0210: 保護喪失の原因。
public enum ProtectiveStopLossCause
{
    /// <summary>エントリーと同時に発注した逆指値をブローカーが受理しなかった。</summary>
    RejectedAtEntry,

    /// <summary>滞留中の逆指値が失効した（取消・拒否・期限切れ）。</summary>
    LapsedInFlight,
}

// #331, IADR-0210: 保護喪失への対処。
public enum ProtectiveStopRemediation
{
    /// <summary>未約定のエントリー注文を取り消した（建玉は生じない）。</summary>
    EntryCancelled,

    /// <summary>約定済みの建玉を成行で手仕舞った（手仕舞いレグは CloseIntent で台帳へ結線）。</summary>
    PositionClosed,

    /// <summary>対処も失敗した。逆指値なしの建玉が残っている可能性があり、人手対応を要する（Critical）。</summary>
    None,
}
