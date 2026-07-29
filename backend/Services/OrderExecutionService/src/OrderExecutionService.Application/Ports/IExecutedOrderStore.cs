using AiStockTrading.OrderExecution.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.OrderExecution.Application.Ports;

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
