using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution;

// FR-05, FR-16: 発注結果（注文実体＋スリッページ）の永続化。実運用では PostgreSQL（Slice B）。月報・射影のデータ源。
public interface IExecutedOrderStore
{
    void Save(ExecutionRecord record);

    /// <summary>記録済みの発注結果を新しい順で返す（照会・射影用）。</summary>
    IReadOnlyList<ExecutionRecord> GetAll();

    /// <summary>DecisionId に対応する発注結果を返す（冪等性チェック用。無ければ null）。</summary>
    ExecutionRecord? FindByDecisionId(Guid decisionId);

    /// <summary>
    /// #270, IADR-0113: 非終端（<see cref="OrderStatusLifecycle.IsPending"/>）の記録を古い順に最大
    /// <paramref name="batchSize"/> 件返す（約定状態の追跡対象）。<paramref name="since"/> より古い記録は
    /// 追跡上限を過ぎたものとして除外する（以降は滞留＝リコンサイル／人手の領分）。
    /// </summary>
    IReadOnlyList<ExecutionRecord> FindPendingSince(DateTimeOffset since, int batchSize);

    /// <summary>
    /// FR-10, #958, IADR-0406 決定1: 指定した注文 ID のうち非終端（<see cref="OrderStatusLifecycle.IsPending"/>）の記録を
    /// 古い順に返す。<b>追跡上限は見ない</b>——呼び出し側（約定追跡）が対象を Active な S0 の逆指値レグに絞る。
    /// 空集合なら何も読まずに空を返す。
    /// </summary>
    IReadOnlyList<ExecutionRecord> FindPendingByOrderIds(IReadOnlyCollection<string> orderIds);

    /// <summary>
    /// FR-10, #958, IADR-0406 決定3: 非終端の記録の<b>追跡の起点</b>（<see cref="ExecutionRecord.ExecutedAt"/>）を
    /// <paramref name="trackedFrom"/> へ進める（約定追跡の窓へ戻す）。<b>時刻の列だけ</b>を書き、状態・数量・価格には触れない
    /// （並行に約定追跡が記録を終端にしていても、古い状態で上書きしない）。記録が無い・終端・起点が既に同じか新しいなら
    /// 何もせず false を返す。
    /// </summary>
    bool RenewTracking(string orderId, DateTimeOffset trackedFrom);

    // #820 の 4 巡目監査, IADR-0344 追記(4): FindClosesSince（建玉照会がまだ映していない決済の走査）は撤去した。
    // 持ち分を毎巡回引き直す方式そのものをやめ、保護記録が残保護数量を状態として持つ形へ作り直したため、
    // 決済レグの記録を持ち分の計算に使わない（完了済み S0 行の取消済みレグで持ち分が食われる事故も構造的に消える）。

    /// <summary>
    /// #270, IADR-0113: 追跡で観測した最新のブローカ状態を既存記録へ反映する（<paramref name="orderId"/> で特定）。
    /// 記録が無ければ何もせず false を返す（新規に作らない＝DecisionId 1:1 の不変を壊さない）。
    /// </summary>
    bool UpdateOutcome(
        string orderId,
        OrderStatus status,
        int filledQuantity,
        decimal averagePrice,
        decimal slippageRatio,
        DateTimeOffset executedAt);
}
