using AiStockTrading.OrderExecution.Domain;

namespace AiStockTrading.OrderExecution.Application.Ports;

// FR-05, FR-16: 発注結果（注文実体＋スリッページ）の永続化。実運用では PostgreSQL（Slice B）。月報・射影のデータ源。
public interface IExecutedOrderStore
{
    void Save(ExecutionRecord record);

    /// <summary>記録済みの発注結果を新しい順で返す（照会・射影用）。</summary>
    IReadOnlyList<ExecutionRecord> GetAll();

    /// <summary>DecisionId に対応する発注結果を返す（冪等性チェック用。無ければ null）。</summary>
    ExecutionRecord? FindByDecisionId(Guid decisionId);
}
