using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-10, #829, IADR-0346 決定1: 未終端の承認済み新規建て注文の供給（インメモリ・テスト／単体実行用）。
// 本番配線は EfWorkingEntryOrderSource。**同じ意味論**（Open・承認時刻が下限以降・注文アクティビティの終端時刻が無い
// ／行が無い）を 2 つのインメモリストアの合成で再現する（WorkingEntryOrderSourceTests が同じシナリオで両者を検査する）。
public sealed class InMemoryWorkingEntryOrderSource(
    InMemoryPortfolioLedgerStore ledger,
    InMemoryOrderActivityStore activity) : IWorkingEntryOrderSource
{
    public IReadOnlyList<WorkingEntryOrder> GetWorkingEntryOrders(DateTimeOffset approvedAtOrAfter) =>
        ledger.SnapshotApprovals()
            .Where(a => a.Intent.PositionEffect == PositionEffect.Open
                     && a.ApprovedAt >= approvedAtOrAfter
                     && activity.TerminalAtOf(a.DecisionId) is null)
            .Select(a => new WorkingEntryOrder(
                a.DecisionId, a.Intent.Symbol, a.Intent.Market, a.Intent.Side, a.Intent.Quantity, a.Intent.Price,
                a.ApprovedAt, a.Intent.FxRateToBase))
            .ToList();
}
