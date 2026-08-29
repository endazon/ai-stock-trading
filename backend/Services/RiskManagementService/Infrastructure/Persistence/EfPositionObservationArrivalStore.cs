using RiskManagementService.Features.RiskManagement;
using Microsoft.EntityFrameworkCore;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-21, FR-10, FR-06, #463, IADR-0181: 観測が届いた取引日の EF 実装（取引日ごとに 1 行）。
//
// **永続でなければならない。** プロセス内に持つと再起動で「観測が届いていない」へ戻り、
// 供給されていた件数が未供給へ化ける。DbContext は scoped のため本ストアも scoped。
public sealed class EfPositionObservationArrivalStore(RiskManagementDbContext db)
    : IPositionObservationArrivalStore
{
    public void Record(DateOnly tradingDay, DateTimeOffset observedAt)
    {
        var row = db.PositionObservationDays.Find(tradingDay);

        if (row is null)
        {
            db.PositionObservationDays.Add(new PositionObservationDayRow
            {
                TradingDay = tradingDay,
                LastObservedAtUtc = observedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else if (observedAt > row.LastObservedAtUtc)
        {
            // 同一取引日の複数観測では最新を保つ（**判定に使うのは行の存在**であり、時刻は診断用である）。
            row.LastObservedAtUtc = observedAt;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            // 既に同等以上が記録されている＝書き込みを起こさない。
            return;
        }

        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException)
        {
            // **その日の行が既に在る（別レプリカが同じ日を記録した）** ことが実務上の主因であり、
            // その場合「観測が届いた日である」という記録の目的は達成されている。
            //
            // ⚠️ **接続断など真の書き込み失敗も同じ分岐に入る**（既存 7 ストアと同じ広域 catch の踏襲だが、
            // 本ストアは観測のたびに呼ばれるため頻度が高い）。**倒れる向きは「記録できていない」であり、
            // 期間判定はその日を未観測として扱う＝未供給へ倒れる**ため、fail-open にはならない。
            // 観測の記録に失敗したことを理由に観測の処理そのもの（推定・乖離検知）を止めない。
            db.ChangeTracker.Clear();
        }
    }

    public IReadOnlyList<DateOnly> GetObservedDaysBetween(DateOnly fromInclusive, DateOnly toInclusive) =>
        db.PositionObservationDays
            .AsNoTracking()
            .Where(r => r.TradingDay >= fromInclusive && r.TradingDay <= toInclusive)
            .Select(r => r.TradingDay)
            .ToList();
}
