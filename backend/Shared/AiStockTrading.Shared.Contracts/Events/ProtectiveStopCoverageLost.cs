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
//
// 🔴 FR-10, FR-11, UC-06, #848, IADR-0117（2026-09-19 追記・改定 7）:
// **Remediation=CloseDispatchIndeterminate も CloseDecisionId / CloseIntent を持つ。** 成行手仕舞いを送信したが
// 結果を確認できていない（届いたか不明）状態であり、手仕舞いは証券会社側で**生きているかもしれない**。
// リスク管理は PositionClosed と同じく承認行を足し、**処理中の決済として在庫を押さえる**
//（押さえないと利用者の手仕舞い要求が通り、同じ株数に 2 本の決済が並ぶ＝二重決済でショート化）。
// None で代用してはならない —— None は手仕舞いレグを運ばない約束であり、通知の文面も
// 「解消にも失敗した」になって、読んだ人に手で成行を重ねさせる。
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

    /// <summary>
    /// #848, IADR-0117（改定 7）: 成行手仕舞いを**送信したが結果を確認できていない**（届いたか不明）。
    /// システムは**注文を重ねない**（予約を Reserved のまま残し、同じ DecisionId では再送しない）。
    /// CloseDecisionId / CloseIntent を持ち、台帳は処理中の決済として押さえる。人手の確認を要する（Critical）。
    /// 🔴 列挙の**末尾へ足している**（既存値の序数を動かさない）。
    /// </summary>
    CloseDispatchIndeterminate,
}
