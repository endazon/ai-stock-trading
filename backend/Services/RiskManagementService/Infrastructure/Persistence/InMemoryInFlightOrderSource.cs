using AiStockTrading.Shared.Contracts.Trading;
using RiskManagementService.Features.RiskManagement;

namespace RiskManagementService.Infrastructure.Persistence;

// NFR, #1502, IADR-0600: 未終端の注文（新規建て・決済）の InMemory 実装（試験用）。**EfInFlightOrderSource と同じ意味論**
// （承認時刻が下限以降・注文アクティビティが終端でない〔行なしを含む〕・予約は承認行が無く無効でないもの）。
public sealed class InMemoryInFlightOrderSource(
    InMemoryPortfolioLedgerStore ledger,
    InMemoryOrderActivityStore activity,
    InMemoryEntryReservationStore? reservations = null) : IInFlightOrderSource
{
    public IReadOnlyList<InFlightOrder> GetInFlightOrders(DateTimeOffset approvedAtOrAfter)
    {
        var approvals = ledger.SnapshotApprovals();
        var result = approvals
            .Where(a => a.ApprovedAt >= approvedAtOrAfter && activity.TerminalAtOf(a.DecisionId) is null)
            .Select(a => new InFlightOrder(
                a.Intent.Symbol, a.Intent.Market, a.Intent.PositionEffect, InFlightOrderState.Approved, a.ApprovedAt))
            .ToList();

        if (reservations is not null)
        {
            var approvedIds = approvals.Select(a => a.DecisionId).ToHashSet();
            result.AddRange(reservations.Snapshot()
                .Where(r => r.ApprovedAt >= approvedAtOrAfter
                         && !r.IsVoided
                         && !approvedIds.Contains(r.DecisionId)
                         && activity.TerminalAtOf(r.DecisionId) is null)
                .Select(r => new InFlightOrder(r.Symbol, r.Market, PositionEffect.Open, InFlightOrderState.Reserved, r.ApprovedAt)));
        }

        return result;
    }
}
