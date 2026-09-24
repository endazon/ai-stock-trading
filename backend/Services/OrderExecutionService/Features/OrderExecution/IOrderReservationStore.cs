namespace OrderExecutionService.Features.OrderExecution;

// #131, FR-05, IADR-0057: 発注の3相（予約 → 発注 → 確定）における予約の状態。
public enum OrderDispatchState
{
    /// <summary>発注に着手した（ブローカへ送ったか否かは不明）。この状態での再処理は再発注しない。</summary>
    Reserved = 0,

    /// <summary>発注結果の永続化まで完了した。</summary>
    Completed = 1,

    /// <summary>
    /// 🔴 FR-05, FR-10, UC-06, #876, IADR-0398: <b>見送った</b>（ブローカーへ送っていないと確定している）終端。
    /// 見送り（<c>OrderDispatchForgone</c>）を発行する<b>前</b>に記録する。この DecisionId は<b>二度と発注しない</b>
    /// ——同じ承認が再配送されても、発注執行は相 1 の直後でこの状態を見て何もせずに戻る
    /// （IADR-0211 決定 3「再発注は次の取引判断からのみ」）。取引台帳は見送りを受けて在庫を戻すため、
    /// 後から同じ DecisionId で送ると、台帳が押さえていない決済が生きる（二重決済でショート化）。
    /// <para>
    /// 突合（<see cref="IOrderReservationStore.FindStalledReserved"/>）にも保持期間パージ
    /// （<see cref="IOrderReservationStore.PurgeCompletedBefore"/>）にも載らず、
    /// <see cref="IOrderReservationStore.Release"/> でも消えない。<b>末尾へ追加する</b>（序数 2。整数列のため Migration 不要）。
    /// </para>
    /// </summary>
    Forgone = 2,
}

// 🔴 FR-05, FR-10, #876, IADR-0398: 見送りを予約表へ記録した結果。**4 つを取り違えない**。
// 見送りを主張してよい（＝OrderDispatchForgone を発行してよい）のは Recorded と AlreadyForgone だけである。
public enum ForgoneRecordOutcome
{
    /// <summary>この呼び出しで見送りを記録した。</summary>
    Recorded = 0,

    /// <summary>既に見送りとして記録されていた（冪等。記録の時刻は動かさない）。</summary>
    AlreadyForgone = 1,

    /// <summary>
    /// 🔴 別の配送が同じ DecisionId の予約（Reserved＝発注に着手済み・送ったか不明）を持っている。
    /// <b>見送りを主張してはならない</b>（主張すると、生きているかもしれない注文の在庫の押さえを台帳が解く）。行は変えない。
    /// </summary>
    HeldByReservation = 2,

    /// <summary>🔴 発注結果を確定済み（Completed＝発注済み）。<b>見送りを主張してはならない</b>。行は変えない。</summary>
    AlreadyCompleted = 3,
}

// #131, FR-05, IADR-0057: 発注前に確保する DecisionId の予約。
// #137, IADR-0059: CompletedAt はパージの述語（終端行の経過時間）に用いる。Reserved の間は null であり、
// 「null＝未確定＝パージ対象外」が保持期間パージの安全性の要である。
public sealed record OrderDispatchReservation(
    Guid DecisionId,
    OrderDispatchState State,
    DateTimeOffset ReservedAt,
    string? BrokerOrderId,
    DateTimeOffset? CompletedAt = null);

// #131, FR-05, IADR-0057: 発注前 DecisionId 予約のストア。ブローカ発注の「前」に一意予約をコミットし、
// 「発注成功 → 永続化失敗」の窓での二重発注を防ぐ。実運用では PostgreSQL（DecisionId が主キー＝一意制約）。
public interface IOrderReservationStore
{
    /// <summary>
    /// DecisionId を予約する。新規に確保できたら true、既に予約が存在する（＝再配送・並行配送）なら false。
    /// 実装は true を返す前に予約をコミットし、他プロセスから観測可能にすること（戻った時点で確定していない
    /// と、発注前予約の意味が無くなる）。ブローカ発注より前に呼ぶのは呼び出し側の責務である。
    /// </summary>
    bool TryReserve(Guid decisionId, DateTimeOffset reservedAt);

    /// <summary>発注結果の永続化後に予約を Completed へ確定する（ブローカ注文 ID を記録する）。</summary>
    void MarkCompleted(Guid decisionId, string brokerOrderId, DateTimeOffset completedAt);

    /// <summary>予約を返す（無ければ null）。</summary>
    OrderDispatchReservation? Find(Guid decisionId);

    /// <summary>
    /// #141, IADR-0074: 自動リコンサイルの対象になる**滞留 Reserved**（<see cref="OrderDispatchState.Reserved"/>
    /// かつ <c>ReservedAt</c> が <paramref name="reservedBefore"/> **より古い**）を、<c>ReservedAt</c> の昇順で
    /// 最大 <paramref name="batchSize"/> 件返す。<paramref name="reservedBefore"/> は再配送窓の外側に置くこと
    /// （in-flight の発注を滞留と誤認しないため）。#131 の <c>State</c> インデックスで洗い出せる。
    /// </summary>
    IReadOnlyList<OrderDispatchReservation> FindStalledReserved(DateTimeOffset reservedBefore, int batchSize);

    /// <summary>
    /// #141, IADR-0074: **未発注と確定した** Reserved 予約を解放（削除）する。解放後は元の OrderApproved 再配送が
    /// 改めて予約→発注できる。<see cref="OrderDispatchState.Reserved"/> の行**のみ**削除し、削除したら true を返す。
    /// 見つからない／Reserved でない（Completed 等）場合は何もせず false を返す（終端行は決して消さない安全ガード）。
    /// </summary>
    bool Release(Guid decisionId);

    /// <summary>
    /// 🔴 FR-05, FR-10, #876, IADR-0398: 予約を取る<b>前</b>に見送った DecisionId を <see cref="OrderDispatchState.Forgone"/>
    /// として記録する。行が無ければ挿入して <see cref="ForgoneRecordOutcome.Recorded"/>、既に Forgone なら
    /// <see cref="ForgoneRecordOutcome.AlreadyForgone"/>。<b>Reserved / Completed の行は変えず</b>、それぞれ
    /// <see cref="ForgoneRecordOutcome.HeldByReservation"/> / <see cref="ForgoneRecordOutcome.AlreadyCompleted"/> を返す
    /// （そこに在る予約は別の配送のものであり、送ったか不明または送った）。
    /// 実装は戻る前に記録をコミットすること（<see cref="TryReserve"/> と同じ。見送りの発行より先に観測可能にする）。
    /// </summary>
    ForgoneRecordOutcome TryRecordForgone(Guid decisionId, DateTimeOffset forgoneAt);

    /// <summary>
    /// 🔴 FR-05, FR-10, #876, IADR-0398: <b>呼び出し側が自分で取った</b> Reserved の予約を、ブローカーへ届き得ない段階の失敗
    /// （接続確立の失敗＝確実に未発注）を受けて <see cref="OrderDispatchState.Forgone"/> へ移す
    /// （従来の <see cref="Release"/>＝削除の代わり。削除すると同じ承認の再配送が予約を取り直して発注できてしまう）。
    /// Reserved なら移して <see cref="ForgoneRecordOutcome.Recorded"/>、既に Forgone なら
    /// <see cref="ForgoneRecordOutcome.AlreadyForgone"/>、Completed なら行を変えず
    /// <see cref="ForgoneRecordOutcome.AlreadyCompleted"/>。行が無ければ <see cref="TryRecordForgone"/> と同じく扱う。
    /// <b>送信後の失敗（届いたか不明）に使ってはならない</b>——その予約は Reserved のまま据え置く（IADR-0117 改定 6）。
    /// </summary>
    ForgoneRecordOutcome MarkReservationForgone(Guid decisionId, DateTimeOffset forgoneAt);

    /// <summary>
    /// NFR（運用）, #137, IADR-0059: <see cref="OrderDispatchState.Completed"/> かつ <c>CompletedAt</c> が
    /// <paramref name="cutoff"/> **より古い**（境界ちょうどは含まない）終端行を、最大
    /// <paramref name="batchSize"/> 件パージし削除件数を返す。
    ///
    /// **<see cref="OrderDispatchState.Reserved"/> の予約は、どれだけ古くても決してパージしないこと。**
    /// 🔴 #876, IADR-0398: **<see cref="OrderDispatchState.Forgone"/> もパージしない**（消すと同じ承認の再配送が
    /// 予約を取り直して発注できる＝見送りを受けて在庫を戻した台帳が押さえていない注文が生きる）。
    /// Reserved は「ブローカへ発注済みか不明」を意味し、消せば再配送で二重発注＝実弾では実損になる
    /// （IADR-0057 が防ぐものをパージが破壊する）。滞留 Reserved の解消は #141（自動リコンサイル）または
    /// 人手の判断であって、時間経過ではない。
    /// </summary>
    int PurgeCompletedBefore(DateTimeOffset cutoff, int batchSize);
}
