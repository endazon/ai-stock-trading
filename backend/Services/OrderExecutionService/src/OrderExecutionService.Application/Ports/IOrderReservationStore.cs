namespace AiStockTrading.OrderExecution.Application.Ports;

// #131, FR-05, IADR-0057: 発注の3相（予約 → 発注 → 確定）における予約の状態。
public enum OrderDispatchState
{
    /// <summary>発注に着手した（ブローカへ送ったか否かは不明）。この状態での再処理は再発注しない。</summary>
    Reserved = 0,

    /// <summary>発注結果の永続化まで完了した。</summary>
    Completed = 1,
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
    /// NFR（運用）, #137, IADR-0059: <see cref="OrderDispatchState.Completed"/> かつ <c>CompletedAt</c> が
    /// <paramref name="cutoff"/> **より古い**（境界ちょうどは含まない）終端行を、最大
    /// <paramref name="batchSize"/> 件パージし削除件数を返す。
    ///
    /// **<see cref="OrderDispatchState.Reserved"/> の予約は、どれだけ古くても決してパージしないこと。**
    /// Reserved は「ブローカへ発注済みか不明」を意味し、消せば再配送で二重発注＝実弾では実損になる
    /// （IADR-0057 が防ぐものをパージが破壊する）。滞留 Reserved の解消は #141（自動リコンサイル）または
    /// 人手の判断であって、時間経過ではない。
    /// </summary>
    int PurgeCompletedBefore(DateTimeOffset cutoff, int batchSize);
}
