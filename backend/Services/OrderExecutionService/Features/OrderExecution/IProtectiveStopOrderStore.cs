using OrderExecutionService.Domain;

namespace OrderExecutionService.Features.OrderExecution;

// FR-10, #331, IADR-0210 決定6: 保護逆指値レグの記録ストア。EntryDecisionId につき高々 1 行
// （最新試行のみ）。ProtectiveStopGuard の巡回対象（Active）の洗い出しの権威。実運用では PostgreSQL。
public interface IProtectiveStopOrderStore
{
    /// <summary>保存する（EntryDecisionId で upsert。再発注は同キーの上書き＝試行の置き換え）。</summary>
    void Save(ProtectiveStopOrder stop);

    /// <summary>EntryDecisionId で引く（無ければ null）。</summary>
    ProtectiveStopOrder? Find(Guid entryDecisionId);

    /// <summary>
    /// Active な記録を古い順に最大 <paramref name="batchSize"/> 件返す（ProtectiveStopGuard の巡回対象）。
    /// </summary>
    IReadOnlyList<ProtectiveStopOrder> FindActive(int batchSize);
}
