using System.Collections.Concurrent;
using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Infrastructure.Persistence;

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

    // FR-20, #386, IADR-0149 決定2: 承認済み注文の建玉効果を DecisionId で引く（未承認は null＝不明）。
    public PositionEffect? FindApprovedPositionEffect(Guid decisionId) =>
        _approvals.TryGetValue(decisionId, out var approval) ? approval.Intent.PositionEffect : null;

    // FR-19, #425, IADR-0165: 承認 Intent を DecisionId で引く（未承認は null＝不明）。
    public OrderIntent? FindApprovedIntent(Guid decisionId) =>
        _approvals.TryGetValue(decisionId, out var approval) ? approval.Intent : null;

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

    // #292, IADR-0117: 処理中の決済数量（EfPortfolioLedgerStore と同一の意味論）。
    public int GetInFlightCloseQuantity(string symbol, Market market, DateTimeOffset approvedAtOrAfter)
    {
        // DecisionId ごとの約定累計。1 承認に複数の注文行が対応し得る形（リコンサイル経路）でも取りこぼさない。
        var filledByDecision = new Dictionary<Guid, int>();
        foreach (var fill in _fills.Values)
        {
            filledByDecision.TryGetValue(fill.DecisionId, out var current);
            filledByDecision[fill.DecisionId] = current + fill.FilledQuantity;
        }

        var total = 0;
        foreach (var (decisionId, approval) in _approvals)
        {
            var intent = approval.Intent;
            if (intent.PositionEffect != PositionEffect.Close
                || intent.Symbol != symbol
                || intent.Market != market
                || approval.ApprovedAt < approvedAtOrAfter)
            {
                continue;
            }

            // 未約定 = 承認数量 − 約定累計。約定が承認を超えた場合（部分列挙・訂正）も負に振れさせない。
            total += Math.Max(0, intent.Quantity - filledByDecision.GetValueOrDefault(decisionId));
        }

        return total;
    }

    private sealed record ApprovalRecord(OrderIntent Intent, DateTimeOffset ApprovedAt);

    private sealed record FillRecord(Guid DecisionId, int FilledQuantity, decimal AveragePrice, DateTimeOffset ExecutedAt);
}
