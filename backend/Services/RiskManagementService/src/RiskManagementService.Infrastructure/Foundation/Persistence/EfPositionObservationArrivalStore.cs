using AiStockTrading.RiskManagement.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence;

// FR-21, FR-10, FR-06, #463, IADR-0181: 観測の到達（最終観測時刻）の EF 実装（単一行）。
//
// **永続でなければならない。** プロセス内に持つと再起動で「観測が一度も届いていない」へ戻り、
// 供給されていた件数が未供給へ化ける。DbContext は scoped のため本ストアも scoped。
internal sealed class EfPositionObservationArrivalStore(RiskManagementDbContext db)
    : IPositionObservationArrivalStore
{
    public DateTimeOffset? GetLastObservedAt() =>
        db.PositionObservationArrivals.Find(SingletonKeys.Id)?.LastObservedAtUtc;

    public void Record(DateTimeOffset observedAt)
    {
        var row = db.PositionObservationArrivals.Find(SingletonKeys.Id);

        if (row is null)
        {
            db.PositionObservationArrivals.Add(new PositionObservationArrivalRow
            {
                Id = SingletonKeys.Id,
                LastObservedAtUtc = observedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else if (observedAt > row.LastObservedAtUtc)
        {
            // **単調前進のみ。** 順序保証の無いバスでは後着の古い観測が届き得る。巻き戻すと
            // 「供給されていた」状態が後から「未供給」寄りへ落ちる。
            row.LastObservedAtUtc = observedAt;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            // 既に同等以上の時刻が記録されている＝何もしない（書き込みも起こさない）。
            return;
        }

        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException)
        {
            // 初回行の同時挿入（主キー衝突）。**別レプリカが同じ前進を行ったということであり、
            // 記録という目的は達成されている。** 観測の到達の記録に失敗したことを理由に
            // 観測の処理そのものを失敗させない（記録は統制の副次的な証跡であり、
            // 落とすと推定・乖離検知まで止まる）。
            db.ChangeTracker.Clear();
        }
    }
}
