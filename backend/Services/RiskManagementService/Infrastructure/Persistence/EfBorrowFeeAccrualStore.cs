using RiskManagementService.Features.RiskManagement;
using Microsoft.EntityFrameworkCore;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-10, FR-11, SC-03, UC-06, #465, ADR-0027, IADR-0183: 借株料の日次計上の EF 実装。
//
// **永続でなければならない。** 累計は建玉の生涯にわたって積み上がる値であり、プロセス内に持つと
// 再起動で費用が消える —— **実費より小さい累計が「正しい累計」として報告される**（過小計上へ倒れる）。
// DbContext は scoped のため本ストアも scoped。
// #714: 可視性は public（兄弟の EF ストア・InMemoryBorrowFeeAccrualStore と揃える）。
// 順序固定の競合再現テストが本クラスを直接組み立てるため、internal では届かない。
public sealed class EfBorrowFeeAccrualStore(RiskManagementDbContext db) : IBorrowFeeAccrualStore
{
    public bool Record(BorrowFeeAccrual accrual)
    {
        ArgumentNullException.ThrowIfNull(accrual);

        // 既に同じ日の行があれば**書き換えない**（最初の計上を正とする）。後から別の料率で上書きすると、
        // 監査へ残した日次の内訳（BorrowFeeAccrued）と台帳が食い違い、内訳から累計を再現できなくなる。
        if (db.BorrowFeeAccruals.Find(accrual.Symbol, accrual.Market, accrual.TradingDay) is not null)
        {
            return false;
        }

        db.BorrowFeeAccruals.Add(new BorrowFeeAccrualRow
        {
            Symbol = accrual.Symbol,
            Market = accrual.Market,
            TradingDay = accrual.TradingDay,
            RateAnnual = accrual.RateAnnual,
            PositionValueUsd = accrual.PositionValueUsd,
            AmountUsd = accrual.AmountUsd,
            AccruedAtUtc = accrual.AccruedAt,
        });

        return SaveNewRow(() =>
            db.BorrowFeeAccruals.Find(accrual.Symbol, accrual.Market, accrual.TradingDay) is not null);
    }

    public bool RecordUnavailable(BorrowFeeUnavailableDay day)
    {
        ArgumentNullException.ThrowIfNull(day);

        if (db.BorrowFeeUnavailableDays.Find(day.Symbol, day.Market, day.TradingDay) is not null)
        {
            return false;
        }

        db.BorrowFeeUnavailableDays.Add(new BorrowFeeUnavailableDayRow
        {
            Symbol = day.Symbol,
            Market = day.Market,
            TradingDay = day.TradingDay,
            Reason = day.Reason,
            ObservedAtUtc = day.ObservedAt,
        });

        return SaveNewRow(() =>
            db.BorrowFeeUnavailableDays.Find(day.Symbol, day.Market, day.TradingDay) is not null);
    }

    public IReadOnlyList<BorrowFeeAccrual> GetAccrualsBetween(DateOnly fromInclusive, DateOnly toInclusive) =>
        [.. db.BorrowFeeAccruals
            .AsNoTracking()
            .Where(r => r.TradingDay >= fromInclusive && r.TradingDay <= toInclusive)
            .OrderBy(r => r.TradingDay)
            .ThenBy(r => r.Symbol)
            .Select(r => new BorrowFeeAccrual(
                r.Symbol, r.Market, r.TradingDay, r.RateAnnual, r.PositionValueUsd, r.AmountUsd, r.AccruedAtUtc))];

    public IReadOnlyList<BorrowFeeUnavailableDay> GetUnavailableDaysBetween(
        DateOnly fromInclusive, DateOnly toInclusive) =>
        [.. db.BorrowFeeUnavailableDays
            .AsNoTracking()
            .Where(r => r.TradingDay >= fromInclusive && r.TradingDay <= toInclusive)
            .OrderBy(r => r.TradingDay)
            .ThenBy(r => r.Symbol)
            .Select(r => new BorrowFeeUnavailableDay(
                r.Symbol, r.Market, r.TradingDay, r.Reason, r.ObservedAtUtc))];

    // 主キー衝突（＝別レプリカが同じ建玉・同じ日を先に書いた）は**目的が達成されている**ため false を返す。
    //
    // FR-10, FR-11, #714, IADR-0317, IADR-0319: **競合の判定は例外の型ではなく「対象行が実在するか」で行う。**
    // 一意キー違反の例外型はプロバイダごとに違う（relational は DbUpdateException、InMemory は
    // ArgumentException）ため、型を列挙すると取りこぼした側だけが素通りする。
    // <paramref name="rowExists"/> は呼び出し側が渡す「自分が書こうとした行の実在」判定である
    // （計上と未供給で別テーブルを見るため、ここでは決め打ちできない）。
    private bool SaveNewRow(Func<bool> rowExists)
    {
        try
        {
            db.SaveChanges();
            return true;
        }
        catch (Exception ex) when (ex is DbUpdateException or ArgumentException)
        {
            db.ChangeTracker.Clear();
            if (rowExists())
            {
                // 別レプリカが同じ建玉・同じ日を先に書いた＝記録の目的は達成されている（冪等）。
                return false;
            }

            // 🔴 **行が生まれていない＝競合ではなく本物の書き込み失敗である。握り潰さない。**
            // 従来は接続断なども false に化け、集計はその日を計上日数にも未供給日数にも数えなかった ——
            // **合計が実費より小さく出る（費用を過小に見せる）** 側へ無言で倒れていた
            // （IADR-0183 が残余リスクとして明記していたもの）。再送出すれば再配送で再試行される。
            throw;
        }
    }
}
