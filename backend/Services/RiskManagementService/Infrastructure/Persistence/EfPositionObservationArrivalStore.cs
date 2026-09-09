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
        // FR-21, #714, IADR-0317, IADR-0319: **競合の判定は例外の型ではなく「その日の行が実在するか」で行う。**
        // 一意キー違反の例外型はプロバイダごとに違う（relational は DbUpdateException、InMemory は
        // ArgumentException）ため、型を列挙すると取りこぼした側だけが素通りする。
        catch (Exception ex) when (ex is DbUpdateException or ArgumentException)
        {
            db.ChangeTracker.Clear();

            // **その日の行が既に在る（別レプリカが同じ日を記録した）** ことが実務上の主因であり、
            // その場合「観測が届いた日である」という記録の目的は達成されている。
            if (db.PositionObservationDays.AsNoTracking().Any(r => r.TradingDay == tradingDay))
            {
                return;
            }

            // 🔴 **行が生まれていない＝競合ではなく本物の書き込み失敗である。握り潰さない。**
            // 従来は接続断などの真の失敗も無言で飲み込んでいた。倒れる向きが「未供給」で安全側だとしても、
            // **記録できていないことが呼び出し側から見えない**のは統制として弱い（メッセージが再配送されず、
            // 「観測は届いたが記録だけが落ちた日」が誰にも気づかれないまま残る）。
            throw;
        }
    }

    public IReadOnlyList<DateOnly> GetObservedDaysBetween(DateOnly fromInclusive, DateOnly toInclusive) =>
        db.PositionObservationDays
            .AsNoTracking()
            .Where(r => r.TradingDay >= fromInclusive && r.TradingDay <= toInclusive)
            .Select(r => r.TradingDay)
            .ToList();
}
