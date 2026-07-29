using System.Collections.Concurrent;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.Adapters;

// FR-10, FR-05, IADR-0018: 取引台帳のインメモリ実装（テスト・単体実行用）。PostgreSQL 永続化は Worker の
// EfPortfolioLedgerStore で差し替える。承認は DecisionId、約定は OrderId で冪等に保持する。
public sealed class InMemoryPortfolioLedgerStore : IPortfolioLedgerStore
{
    private readonly ConcurrentDictionary<Guid, ApprovalRecord> _approvals = new();
    private readonly ConcurrentDictionary<string, FillRecord> _fills = new();

    public void AppendApproval(Guid decisionId, OrderIntent intent, DateTimeOffset approvedAt)
    {
        ArgumentNullException.ThrowIfNull(intent);
        _approvals.TryAdd(decisionId, new ApprovalRecord(intent, approvedAt));
    }

    public bool AppendFill(Guid decisionId, string orderId, int filledQuantity, decimal averagePrice, DateTimeOffset executedAt)
    {
        if (!_approvals.ContainsKey(decisionId))
            return false;

        // #270, IADR-0113: 単調 upsert（EfPortfolioLedgerStore と同一の意味論）。約定数量は累積値であり、
        // 累積が増えたときだけ更新する。再送・順序前後で二重計上も巻き戻りも起こさない。
        _fills.AddOrUpdate(
            orderId,
            _ => new FillRecord(decisionId, filledQuantity, averagePrice, executedAt),
            (_, current) => filledQuantity > current.FilledQuantity
                ? new FillRecord(decisionId, filledQuantity, averagePrice, executedAt)
                : current);
        return true;
    }

    public IReadOnlyList<LedgerFill> GetFills()
    {
        var result = new List<LedgerFill>();
        foreach (var fill in _fills.Values)
        {
            if (!_approvals.TryGetValue(fill.DecisionId, out var approval))
                continue;

            var intent = approval.Intent;
            // IADR-0107: 承認 Intent の換算レートを台帳の約定に引き継ぐ（金額集計は基準通貨で行う）。
            result.Add(new LedgerFill(
                intent.Symbol, intent.Market, intent.Side, intent.PositionEffect,
                fill.FilledQuantity, fill.AveragePrice, fill.ExecutedAt, intent.StopLossPrice, intent.FxRateToBase));
        }

        return result;
    }

    private sealed record ApprovalRecord(OrderIntent Intent, DateTimeOffset ApprovedAt);

    private sealed record FillRecord(Guid DecisionId, int FilledQuantity, decimal AveragePrice, DateTimeOffset ExecutedAt);
}
