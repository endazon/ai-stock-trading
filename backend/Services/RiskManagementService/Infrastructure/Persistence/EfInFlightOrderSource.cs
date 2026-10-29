using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.EntityFrameworkCore;
using RiskManagementService.Features.RiskManagement;

namespace RiskManagementService.Infrastructure.Persistence;

// NFR, #1502, IADR-0600: 未終端の注文（新規建て・決済）の EF 実装。読み方は EfWorkingEntryOrderSource と同じで、
// 承認行の効果（新規建て・決済）で絞らない点だけが違う。予約（承認行がまだ無い新規建て）も同じ規則で返す。
// 🔴 承認行と予約を 1 つの SQL 文（UNION ALL）で読む（2 文に分けると、2 文の間に承認行がコミットされた注文がどちらにも現れない。
//    EfWorkingEntryOrderSource の #1319 の監査と同じ理由）。
public sealed class EfInFlightOrderSource(RiskManagementDbContext db) : IInFlightOrderSource
{
    public IReadOnlyList<InFlightOrder> GetInFlightOrders(DateTimeOffset approvedAtOrAfter) =>
        Query(approvedAtOrAfter)
            .AsEnumerable()
            .Select(r => new InFlightOrder(r.Symbol, r.Market, r.PositionEffect, r.State, r.ApprovedAt))
            .ToList();

    /// <summary>SQL へ訳される問い合わせ（試験が Npgsql の訳を確かめるために公開する）。</summary>
    public IQueryable<Row> Query(DateTimeOffset approvedAtOrAfter)
    {
        var approved =
            from a in db.ApprovedOrders.AsNoTracking()
            where a.ApprovedAt >= approvedAtOrAfter
            join o in db.OrderActivities.AsNoTracking() on a.DecisionId equals o.DecisionId into activities
            from o in activities.DefaultIfEmpty()
            where o == null || o.TerminalAt == null
            select new Row { Symbol = a.Symbol, Market = a.Market, PositionEffect = a.PositionEffect, State = InFlightOrderState.Approved, ApprovedAt = a.ApprovedAt };

        var reserved =
            from r in db.EntryReservations.AsNoTracking()
            where r.ApprovedAt >= approvedAtOrAfter
                  && r.VoidedAt == null
                  && !db.ApprovedOrders.Any(a => a.DecisionId == r.DecisionId)
            join o in db.OrderActivities.AsNoTracking() on r.DecisionId equals o.DecisionId into activities
            from o in activities.DefaultIfEmpty()
            where o == null || o.TerminalAt == null
            select new Row { Symbol = r.Symbol, Market = r.Market, PositionEffect = PositionEffect.Open, State = InFlightOrderState.Reserved, ApprovedAt = r.ApprovedAt };

        return approved.Concat(reserved);
    }

    // 🔴 構築子ではなくメンバー初期化で射影する（EF は構築子の射影〔クライアント側〕の後に集合演算を SQL へ訳せない）。
    public sealed class Row
    {
        public required string Symbol { get; init; }

        public Market Market { get; init; }

        public PositionEffect PositionEffect { get; init; }

        public InFlightOrderState State { get; init; }

        public DateTimeOffset ApprovedAt { get; init; }
    }
}
