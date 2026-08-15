using AiStockTrading.Audit.Application.State;

namespace AiStockTrading.Audit.Application.Ports;

// FR-11, UC-07, IADR-0019: 監査台帳（追記専用）。Id（=MessageId）で冪等。相関・期間で照会する。
public interface IAuditEventStore
{
    /// <summary>監査記録を追記する。既存 Id（再送）は無視する（冪等）。</summary>
    void Append(AuditEntry entry);

    /// <summary>相関ID（注文の DecisionId／市場の EventId）に紐づく記録を時系列（OccurredAt 昇順）で返す。</summary>
    IReadOnlyList<AuditEntry> GetByCorrelation(Guid correlationId);

    /// <summary>直近の記録を OccurredAt 降順で最大 limit 件返す。</summary>
    IReadOnlyList<AuditEntry> GetRecent(int limit);

    /// <summary>
    /// 指定した種別の記録を、期間 <c>[fromInclusive, toExclusive)</c> で時系列（OccurredAt 昇順）に返す。
    /// <para>
    /// 🔴 <b>件数の上限を持たない。</b> 期間の集計に使うため、
    /// <b>取りこぼしたことが赤くならない</b>形（上限つき取得）にしてはならない
    /// （#381 供給結線・IADR-0199 決定2）。
    /// </para>
    /// <para>
    /// <b>半開区間である。</b> 終端を閉じると<b>その日の最後の 1 秒が落ちる</b>。
    /// </para>
    /// </summary>
    IReadOnlyList<AuditEntry> GetByTypesInPeriod(
        IReadOnlyCollection<string> eventTypes,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive);
}
