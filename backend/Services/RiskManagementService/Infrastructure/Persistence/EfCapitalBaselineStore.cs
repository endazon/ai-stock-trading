using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
//
// FR-10, NFR-07, #889, IADR-0372（2026-09-23 追加）: 🔴 **読み出しの帰結を観測する。**
// 口座照会が残高 0 を返した日は供給側の門が未供給へ倒すため**その取引日の行が 1 行も書かれず**、
// ここは前取引日の正の値を鮮度が切れるまで返し続ける（＝新規建ては止まらない）。
// **値が返っている以上、統制は平常どおり動いて見え、この状態は外から一切見えなかった。**
// **門は変えない**（止めるかどうかは #889 の裁定待ち）。見えるようにするだけである。
public sealed class EfCapitalBaselineStore(
    RiskManagementDbContext db,
    IClock clock,
    CapitalBaselineOptions options,
    ILogger<EfCapitalBaselineStore> logger,
    BusinessMetrics metrics)
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
            metrics.RecordCapitalBaselineRead(CapitalBaselineReadOutcome.UnavailableNoRow);
            return null;
        }

        // 鮮度（計画は「日次でよい」とだけ定め、検査は実装判断・IADR-0354 決定4）。
        // 既定 4 日は 3 連休（金 23:55 ET の観測を火曜の寄り付き 09:30 ET に使う＝実測で最大およそ 3.40 日）を通し、
        // 巡回が 1 営業週にわたり死んでいる状態は通さない幅である。
        if (now - row.ObservedAtUtc > options.MaxAge)
        {
            metrics.RecordCapitalBaselineRead(CapitalBaselineReadOutcome.UnavailableStale);
            return null;
        }

        // 🔴 #869, IADR-0354 決定7: **0 以下の行は「判定できる値」として扱わない。**
        // 供給側（アダプタ）が既に 0 以下を弾いているが、**運用 Runbook が人手の 1 行投入を認めている**ため
        // 読み出し側にも同じ門を置く（本ガードより前に書かれた行にも効く）。
        // 0 を分母にすると比率上限がすべて 0 になり、平常状態でも `DailyLossLimitReached` が立ち、
        // 発注審査がその理由で**翌営業日まで続く日次損失ロックアウトを実際に張る**。
        if (row.EquityInBase <= 0m)
        {
            // 🔴 #889: ここは**人手で 0 を 1 行入れた**経路である（供給側の 0 は行を書かないのでここへ来ない）。
            // 黙って null を返していたため、運用からは「なぜか未供給」としか見えなかった。
            metrics.RecordCapitalBaselineRead(CapitalBaselineReadOutcome.UnavailableNonPositive);
            logger.LogWarning(
                "基準資金の最新行が 0 以下のため未供給として扱います equity={Equity} tradingDay={TradingDay}。"
                    + "人手で投入した行であれば、意図した停止かを確認してください。",
                row.EquityInBase,
                row.TradingDay);
            return null;
        }

        // 🔴 #889, IADR-0372 決定A: **供給できたことと、観測が途切れていないことは別である。**
        // 取引日は米国東部時間の暦日で数え、巡回（既定 5 分）は土日も回るため、**期待される間隔は 1 日**である
        // （IADR-0354 決定2）。それより開いていれば、直前の取引日の観測が 1 件も届いていない
        // ——口座照会が残高 0 を返した日・照会が壊れていた日・プロセスが落ちていた日のいずれかである。
        // 🔴 **原因は区別しない**（読み出し側から区別できないし、危険なのは原因ではなく
        // 「古い分母で統制が回っている」という状態である）。**門は変えない** —— 従来どおり値を返す。
        var gapDays = today.DayNumber - row.TradingDay.DayNumber;
        if (gapDays > 1)
        {
            metrics.RecordCapitalBaselineRead(CapitalBaselineReadOutcome.SuppliedWithGap);
            logger.LogWarning(
                "基準資金は直前の取引日の観測ではありません tradingDay={TradingDay} 経過={GapDays}日 "
                    + "equity={Equity} observedAt={ObservedAt}。"
                    + "口座照会が値を返せない日が続いており（残高 0・照会障害・プロセス停止のいずれか）、"
                    + "鮮度（既定 {MaxAgeDays} 日）が切れるまで**この古い値で新規建てが通り続けます**。",
                row.TradingDay,
                gapDays,
                row.EquityInBase,
                row.ObservedAtUtc,
                options.MaxAgeDays);
        }
        else
        {
            metrics.RecordCapitalBaselineRead(CapitalBaselineReadOutcome.Supplied);
        }

        return new CapitalBaseline(row.EquityInBase, row.TradingDay, row.ObservedAtUtc);
    }
}
