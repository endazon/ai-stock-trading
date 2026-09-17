using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.EntityFrameworkCore;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-10, #829, IADR-0346 決定1: 未終端の承認済み新規建て注文の EF 実装（Risk 専有 DB）。
// approved_orders（承認 Intent・承認時レート）を order_activity（注文の生死・IADR-0067）へ DecisionId で左結合する。
//
// - **終端は TerminalAt で判定する**（Status ではない）。RecordExecution は Status を無条件に上書きするため、
//   遅着の非終端イベントで Status は巻き戻り得るが、TerminalAt は一度立つと消えない（単調）。
// - **order_activity の行が無い承認は未終端として返す**（射影の到着前＝生きている側へ倒す）。
// - 当日の判定はしない（PortfolioProjection.Project が市場の現地取引日で行う）。
public sealed class EfWorkingEntryOrderSource(RiskManagementDbContext db) : IWorkingEntryOrderSource
{
    public IReadOnlyList<WorkingEntryOrder> GetWorkingEntryOrders(DateTimeOffset approvedAtOrAfter)
    {
        // 発注審査ごとに呼ばれるホットパスの読み取りのため変更追跡は不要。
        var query =
            from a in db.ApprovedOrders.AsNoTracking()
            where a.PositionEffect == PositionEffect.Open && a.ApprovedAt >= approvedAtOrAfter
            join o in db.OrderActivities.AsNoTracking() on a.DecisionId equals o.DecisionId into activities
            from o in activities.DefaultIfEmpty()
            where o == null || o.TerminalAt == null
            select new
            {
                a.DecisionId,
                a.Symbol,
                a.Market,
                a.Side,
                a.Quantity,
                a.Price,
                a.ApprovedAt,
                a.FxRateToBase,
            };

        return query
            .AsEnumerable()
            // IADR-0107: 列追加前の承認行（FxRateToBase が null）はレート 1＝基準通貨建て（GetFills と同じ扱い）。
            .Select(r => new WorkingEntryOrder(
                r.DecisionId, r.Symbol, r.Market, r.Side, r.Quantity, r.Price, r.ApprovedAt, r.FxRateToBase ?? 1m))
            .ToList();
    }
}
