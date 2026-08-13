using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence;

// FR-10, FR-11, SC-03, UC-06, #465, ADR-0027, IADR-0183: 借株料の日次計上の EF 実装。
//
// **永続でなければならない。** 累計は建玉の生涯にわたって積み上がる値であり、プロセス内に持つと
// 再起動で費用が消える —— **実費より小さい累計が「正しい累計」として報告される**（過小計上へ倒れる）。
// DbContext は scoped のため本ストアも scoped。
internal sealed class EfBorrowFeeAccrualStore(RiskManagementDbContext db) : IBorrowFeeAccrualStore
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

        return SaveNewRow();
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

        return SaveNewRow();
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
    // ⚠️ **接続断など真の書き込み失敗も同じ分岐に入る**（既存ストアと同じ広域 catch の踏襲）。
    // **倒れる向きは「その日は計上されていない」** であり、集計はその日を計上日数にも未供給日数にも
    // 数えない —— 合計が実費より**小さく**出る側であり、費用を過小に見せる。したがって
    // **計上に失敗した日は未供給として残る保証が無い**点は残余リスクとして IADR-0183 に記載する。
    private bool SaveNewRow()
    {
        try
        {
            db.SaveChanges();
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
