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
public sealed record OrderDispatchReservation(
    Guid DecisionId,
    OrderDispatchState State,
    DateTimeOffset ReservedAt,
    string? BrokerOrderId);

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
}
