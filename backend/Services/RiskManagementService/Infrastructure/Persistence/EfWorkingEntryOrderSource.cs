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
// - #1319, IADR-0529: 承認行がまだ無い予約（entry_reservations）も同じ規則で返す（下）。
public sealed class EfWorkingEntryOrderSource(RiskManagementDbContext db) : IWorkingEntryOrderSource
{
    public IReadOnlyList<WorkingEntryOrder> GetWorkingEntryOrders(DateTimeOffset approvedAtOrAfter)
    {
        // 発注審査ごとに呼ばれるホットパスの読み取りのため変更追跡は不要。
        var approved =
            from a in db.ApprovedOrders.AsNoTracking()
            where a.PositionEffect == PositionEffect.Open && a.ApprovedAt >= approvedAtOrAfter
            join o in db.OrderActivities.AsNoTracking() on a.DecisionId equals o.DecisionId into activities
            from o in activities.DefaultIfEmpty()
            where o == null || o.TerminalAt == null
            select new { a.DecisionId, a.Symbol, a.Market, a.Side, a.Quantity, a.Price, a.ApprovedAt, a.FxRateToBase };

        // 🔴 FR-10, #1319, IADR-0529 決定3: 審査が承認したが**承認行がまだ無い**新規建て（予約）も生きている注文として返す。
        // - 承認行がある予約は返さない（承認行の側で数える＝**二重に数えない**。終端・約定は承認行の側の規則に従う）。
        // - 承認行より先に注文アクティビティが終端になった予約（見送り・取消が承認の射影より先に届いた）も返さない。
        // - 無効にした予約（再配送で再発行しなかった・IADR-0529 決定6）も返さない。
        var reserved =
            from r in db.EntryReservations.AsNoTracking()
            where r.ApprovedAt >= approvedAtOrAfter
                  && r.VoidedAt == null
                  && !db.ApprovedOrders.Any(a => a.DecisionId == r.DecisionId)
            join o in db.OrderActivities.AsNoTracking() on r.DecisionId equals o.DecisionId into activities
            from o in activities.DefaultIfEmpty()
            where o == null || o.TerminalAt == null
            select new { r.DecisionId, r.Symbol, r.Market, r.Side, r.Quantity, r.Price, r.ApprovedAt, FxRateToBase = (decimal?)r.FxRateToBase };

        // 🔴 #1319 の監査（実 DB で再現）, IADR-0529 決定3: **承認行と予約を 1 つの SQL 文（UNION ALL）で読む。**
        // （射影は匿名型にする。EF は構築子の射影の後に集合演算を SQL へ訳せない。）
        // 2 つの文に分けると、READ COMMITTED は文ごとに別のスナップショットを取るので、2 文の間に OrderApprovedLedgerHandler が
        // 承認行をコミットすると、1 文目（承認行）は承認行を見ず、2 文目（承認行の無い予約）は承認行を見て予約を外し、
        // その注文が**どちらにも現れない**（1 件余分に承認される）。1 文なら両方の半分が同じスナップショットを読む。
        return approved
            .Concat(reserved)
            .AsEnumerable()
            // IADR-0107: 列追加前の承認行（FxRateToBase が null）はレート 1＝基準通貨建て（GetFills と同じ扱い）。
            .Select(r => new WorkingEntryOrder(
                r.DecisionId, r.Symbol, r.Market, r.Side, r.Quantity, r.Price, r.ApprovedAt, r.FxRateToBase ?? 1m))
            .ToList();
    }

}
