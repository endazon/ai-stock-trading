using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, UC-02, #331, IADR-0210 決定3/4: 保護逆指値が成立しない状態を検知し、
// **逆指値なしの建玉を持たない**ための対処を行った（業務フロー 02「逆指値が成立しない場合の扱い」）。
//
// - Cause=RejectedAtEntry: エントリーと同時の逆指値が未受理だった（未約定なら取消・約定済みなら成行手仕舞い）。
// - Cause=LapsedInFlight: 滞留中の逆指値が失効した（再発注不可→成行手仕舞い）。
// - Remediation=None は**建玉解消も失敗した**状態であり、Critical 通知で人手対応を求める。
//
// 🔴 FR-10, #948, IADR-0369（2026-09-25 追記）: 対処の**その後**は原因（Cause）で分かれる。巡回・撃ち直し・上限・
// 再通知を持つのは保護記録を巡回する常駐ガード（LapsedInFlight）だけである。エントリー同時の経路（RejectedAtEntry）は
// 保護記録を作らず、同じ承認の再配送も相 1 で返って保護喪失を出し直さない——対処の結果によらず**通知は 1 回きり**。
// 各値の説明で原因を見ずに「次の巡回で…」と書かない（偽りの約束の同型: #857 の F1 → #941 → #948）。
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
//
// 🔴 FR-10, #853, IADR-0210（2026-09-25 追記）, IADR-0428: **Remediation=StopDispatchIndeterminate は、CloseDecisionId /
// CloseIntent に「逆指値レグ」を載せる**（成行手仕舞いではない）。逆指値を送信したが届いたか不明であり、取消も成行も
// していない（据え置き）。リスク管理は逆指値が武装されたときと同じ由来で承認行を足す（生きていれば約定を相関できる）。
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

    /// <summary>
    /// 対処も失敗した。逆指値なしの建玉が残っている可能性があり、人手対応を要する（Critical）。
    /// <para>
    /// 🔴 #948, IADR-0369（2026-09-25 追記）: <b>その後は原因で分かれる。</b>
    /// <see cref="ProtectiveStopLossCause.RejectedAtEntry"/>（エントリーの取消に失敗し照会でも約定を確かめられない、
    /// または成行手仕舞いが確実に未発注）は保護記録が無く、<b>撃ち直しも再通知も無い</b>（通知は 1 回きり）。
    /// <see cref="ProtectiveStopLossCause.LapsedInFlight"/>（成行手仕舞いが確実に未発注、または発注先に成行の能力が無い）は
    /// 記録が <c>Active</c> のまま残り、常駐ガードが巡回のたびに改めて評価して（成行を送れる発注先なら）撃ち直す。
    /// 失敗が続くあいだは巡回のたびにこの値が発行される。
    /// </para>
    /// </summary>
    None,

    /// <summary>
    /// #848, IADR-0117（改定 7）: 成行手仕舞いを**送信したが結果を確認できていない**（届いたか不明）。
    /// システムは**注文を重ねない**（予約を Reserved のまま残し、同じ DecisionId では再送しない）。
    /// CloseDecisionId / CloseIntent を持ち、台帳は処理中の決済として押さえる。人手の確認を要する（Critical）。
    /// 🔴 列挙の**末尾へ足している**（既存値の序数を動かさない）。
    /// </summary>
    CloseDispatchIndeterminate,

    /// <summary>
    /// 🔴 #857, IADR-0369: 成行手仕舞いを送り、証券会社が**確認できる形で拒否した**
    /// （<c>Rejected</c> / <c>Cancelled</c> / <c>Expired</c> が<b>返った</b>＝未約定残は二度と約定しない）。
    /// <para>
    /// <b>建玉は残っている。</b>「手仕舞い済み（<see cref="PositionClosed"/>）」と混同してはならない
    /// ——混同すると通知が事実と逆になり、（滞留側では）保護記録が完了して<b>逆指値なしの建玉が巡回対象から外れる</b>。
    /// <see cref="CloseDispatchIndeterminate"/> とも別である（あちらは<b>不明</b>、こちらは<b>確定した拒否</b>）。
    /// </para>
    /// <para>
    /// 🔴 <b>手仕舞いレグ（<c>CloseIntent</c>）は運ばない</b>——送った成行は生きていないため、取引台帳に
    /// 処理中の決済として在庫を押さえさせてはならない（押さえると利用者の手仕舞いが通らなくなる）。
    /// <c>CloseDecisionId</c> は拒否された発注記録との相関のために<b>載せる</b>（この非対称は意図的である）。
    /// 人手対応を要する（Critical）。
    /// </para>
    /// <para>
    /// 🔴 #948, IADR-0369（2026-09-25 追記）: <b>その後は原因で分かれる</b>（旧記述「記録は <c>Active</c> のまま残り、
    /// 次の巡回が改めて評価する」は原因を見ていなかった）。
    /// <see cref="ProtectiveStopLossCause.LapsedInFlight"/>（常駐ガード）: 記録は <c>Active</c> のまま試行番号だけを進め、
    /// 次の巡回が新しい <c>CloseDecisionId</c> で撃ち直す。確認できた拒否が 3 回続くと成行は送らないが、記録は閉じず
    /// 巡回を続け、約 1 時間ごと（と再起動後）にこの値を発行し直す（そのときの <c>CloseDecisionId</c> は null）。
    /// <see cref="ProtectiveStopLossCause.RejectedAtEntry"/>（エントリー同時の建玉解消）: <b>保護記録が無い</b>。
    /// 巡回も撃ち直しも再通知も無く、通知は 1 回きりである（手で手仕舞うまで無保護のまま）。
    /// </para>
    /// 🔴 列挙の**末尾へ足している**（既存値の序数を動かさない）。
    /// </summary>
    CloseRejected,

    /// <summary>
    /// 🔴 FR-10, #853, IADR-0210（2026-09-25 追記）, IADR-0428: <b>保護逆指値（逆指値レグ）を送信したが結果を確認できていない</b>
    /// （届いたか不明）。<b>エントリーの取消も成行手仕舞いもしていない</b>——逆指値が証券会社側で生きていれば、建玉を落とすと
    /// 逆指値が孤立し、発火で反対方向の建玉（ショート）を生むためである。逆指値レグの予約（<c>StopDecisionId</c>）を
    /// <c>Reserved</c> のまま残し、同じレグを送り直さない。
    /// <para>
    /// <c>CloseDecisionId</c> は<b>逆指値レグの DecisionId</b>、<c>CloseIntent</c> は<b>逆指値レグの決済意図</b>である
    /// （台帳は逆指値の武装と同じ由来で承認行を足す＝生きていれば約定を相関でき、処理中の決済として押さえる）。
    /// 保護記録は「送信結果待ち」（注文 ID が空の <c>Active</c>）で残り、常駐ガードが巡回する。原因（Cause）を問わず、
    /// 解決するまで約 1 時間ごと（と再起動のたび）に再発行される。解決は突合（client order id）が発注済みと確定したとき
    /// （注文 ID を採用する）か、予約が解放されたとき（未発注として扱う）。人手の確認を要する（Critical）。
    /// </para>
    /// 🔴 列挙の**末尾へ足している**（既存値の序数を動かさない）。
    /// </summary>
    StopDispatchIndeterminate,
}
