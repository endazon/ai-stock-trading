using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

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

    /// <summary>
    /// FR-10, ADR-0040 決定1（S1）, #820, IADR-0344 決定4: Active なソフトウェア逆指値（S1）のうち、銘柄・市場・エントリー方向が
    /// 一致するものを古い順に返す（損切りライン到達の突き合わせ対象）。
    /// </summary>
    IReadOnlyList<ProtectiveStopOrder> FindActiveSoftwareStops(string symbol, Market market, TradeSide entrySide);

    /// <summary>
    /// FR-10, #820 の 6 巡目監査, IADR-0344 追記(6): <b>完了済み</b>のソフトウェア逆指値（S1）のうち、銘柄・市場・
    /// エントリー方向が一致するものを<b>更新が新しい順</b>に最大 <paramref name="limit"/> 件返す。
    /// <para>
    /// 🔴 <b>用途は「復元してよいか」の門だけ</b>である（<see cref="ProtectiveStopNetting"/>）。
    /// 決済を送って残保護数量が 0 になった行は同じ巡回で完了するが、その決済が<b>受理・未約定</b>のあいだ
    /// 建玉照会はまだ減っていない——完了した行を見ずに「建玉が戻った」と読むと、削った行を復元して
    /// <b>同じ建玉を二度売る</b>（反対建玉）。持ち分（残保護数量）の計算にはこの結果を使わない。
    /// </para>
    /// </summary>
    IReadOnlyList<ProtectiveStopOrder> FindCompletedSoftwareStops(
        string symbol, Market market, TradeSide entrySide, int limit);
}
