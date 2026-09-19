using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.EntityFrameworkCore;
using RiskManagementService.Common.Abstractions;
using RiskManagementService.Features.RiskManagement;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-10, #869, ADR-0041 決定2, IADR-0354: 基準資金（equity）の EF 実装（取引日ごとに 1 行）。
//
// 観測は取引日ごとに畳み、**判定には「当日より前の取引日で最新の行」を返す**。
// 取引日は**米国東部時間の暦日**で数える（TradingDay.Of(instant, Market.UnitedStates)）——
// 計画が定める基準は「前営業日終値時点」であり、終値は市場の現地時刻に属する。
// JST で数えると、米国セッション中（JST の翌日 0 時以降）に日付が変わり、**セッションの途中で
// 基準資金が入れ替わる**（#249 / IADR-0246 が日次統制について是正したのと同じ誤り）。
//
// 🔴 **近似の所在**: 行が持つのは「その取引日で**最後に観測できた**評価額」であって「終値時点の評価額」ではない。
// 巡回（既定 5 分）が生きていれば最後の観測は当該取引日の 23:5x ET であり、終値（16:00 ET）以後・
// 翌セッション開始前のため値は終値ベースと一致する。プロセスが落ちていた等で最後の観測がセッション中
// だった場合のみ、その時点の評価額になる（IADR-0354 決定3 に記録した既知の誤差）。
public sealed class EfCapitalBaselineStore(
    RiskManagementDbContext db,
    IClock clock,
    CapitalBaselineOptions options)
    : ICapitalBaselineStore
{
    public void Record(decimal equityInBase, DateTimeOffset observedAt)
    {
        var tradingDay = TradingDay.Of(observedAt, Market.UnitedStates);
        var row = db.AccountEquityDays.Find(tradingDay);

        if (row is null)
        {
            db.AccountEquityDays.Add(new AccountEquityDayRow
            {
                TradingDay = tradingDay,
                EquityInBase = equityInBase,
                ObservedAtUtc = observedAt,
                UpdatedAt = clock.UtcNow,
            });
        }
        else if (observedAt > row.ObservedAtUtc)
        {
            // 同一取引日の複数観測では**最新**を保つ（終値に最も近い観測を残すため）。
            row.EquityInBase = equityInBase;
            row.ObservedAtUtc = observedAt;
            row.UpdatedAt = clock.UtcNow;
        }
        else
        {
            // 逆行する観測（遅れて届いた古い照会）は無視する。書き込みも起こさない。
            return;
        }

        try
        {
            db.SaveChanges();
        }
        // #714, IADR-0317/0319 と同じ作法: **競合の判定は例外の型ではなく「その日の行が実在するか」で行う**
        // （一意キー違反の型はプロバイダごとに違う）。
        catch (Exception ex) when (ex is DbUpdateException or ArgumentException)
        {
            db.ChangeTracker.Clear();
            if (db.AccountEquityDays.AsNoTracking().Any(r => r.TradingDay == tradingDay))
            {
                return;
            }

            // 🔴 行が生まれていない＝競合ではなく本物の書き込み失敗である。握り潰さない。
            throw;
        }
    }

    public CapitalBaseline? GetCurrent()
    {
        var now = clock.UtcNow;
        var today = TradingDay.Of(now, Market.UnitedStates);

        // **当日の行は見ない。** 当日の観測は日中の評価損益を含み、上限が日中に動く
        // （計画 05_trading-assumptions §5 注記が明示的に禁じた作用）。
        var row = db.AccountEquityDays
            .AsNoTracking()
            .Where(r => r.TradingDay < today)
            .OrderByDescending(r => r.TradingDay)
            .FirstOrDefault();

        if (row is null)
        {
            return null;
        }

        // 鮮度（計画は「日次でよい」とだけ定め、検査は実装判断・IADR-0354 決定4）。
        // 既定 4 日は 3 連休（金 23:55 ET の観測を火曜の寄り付き 09:30 ET に使う＝実測で最大およそ 3.40 日）を通し、
        // 巡回が 1 営業週にわたり死んでいる状態は通さない幅である。
        if (now - row.ObservedAtUtc > options.MaxAge)
        {
            return null;
        }

        // 🔴 #869, IADR-0354 決定7: **0 以下の行は「判定できる値」として扱わない。**
        // 供給側（アダプタ）が既に 0 以下を弾いているが、**運用 Runbook が人手の 1 行投入を認めている**ため
        // 読み出し側にも同じ門を置く（本ガードより前に書かれた行にも効く）。
        // 0 を分母にすると比率上限がすべて 0 になり、平常状態でも `DailyLossLimitReached` が立ち、
        // 発注審査がその理由で**翌営業日まで続く日次損失ロックアウトを実際に張る**。
        if (row.EquityInBase <= 0m)
        {
            return null;
        }

        return new CapitalBaseline(row.EquityInBase, row.TradingDay, row.ObservedAtUtc);
    }
}
