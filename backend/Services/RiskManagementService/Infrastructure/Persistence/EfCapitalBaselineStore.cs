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
// ここは前取引日の正の値を鮮度が切れるまで返し続けていた（＝新規建ては止まらなかった）。
//
// 🔴 FR-10, #1423 M-3, ADR-0073, IADR-0581（2026-10-24 追加）: **門を変えた。** 直前の米国東部暦日に成功した観測が
// 無ければ未供給（＝新規建ての拒否）とする（計画 ADR-0073 決定1・2。IADR-0372 決定A の「門は変えない」を置き換える）。
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
        var reading = Read();
        metrics.RecordCapitalBaselineRead(reading.Outcome);
        switch (reading.Outcome)
        {
            case CapitalBaselineReadOutcome.UnavailableNonPositive:
                // 🔴 #889: ここは**人手で 0 を 1 行入れた**経路である（供給側の 0 は行を書かないのでここへ来ない）。
                // 黙って null を返していたため、運用からは「なぜか未供給」としか見えなかった。
                logger.LogWarning(
                    "基準資金の最新行が 0 以下のため未供給として扱います equity={Equity} tradingDay={TradingDay}。"
                        + "人手で投入した行であれば、意図した停止かを確認してください。",
                    reading.LatestEquityInBase,
                    reading.LatestTradingDay);
                break;
            case CapitalBaselineReadOutcome.UnavailableMissingPreviousDay:
                // FR-10, #1423 M-3, ADR-0073 決定1・2, IADR-0581 決定1: 直前の米国東部暦日に成功した観測が無い＝照会できない。
                logger.LogWarning(
                    "基準資金は直前の米国東部暦日の観測がないため未供給として扱い、新規建てを止めます "
                        + "latestTradingDay={TradingDay} 経過={GapDays}日 equity={Equity}。"
                        + "口座照会が値を返せない日がありました（残高 0・照会障害・プロセス停止のいずれか）。"
                        + "直前の暦日の観測が成功すれば自動で解除されます（手仕舞い・損切りは止めていません）。",
                    reading.LatestTradingDay,
                    reading.GapDays,
                    reading.LatestEquityInBase);
                break;
        }

        return reading.Baseline;
    }

    // FR-10, #1423 M-3, ADR-0073 決定1, IADR-0581 決定1・2: 判定の本体（計器を動かさず、ログも出さない）。
    // 審査（GetCurrent）と、表示（SC-03）・通知の巡回が**同じ判定**を共有するために切り出した。
    public CapitalBaselineReading Read()
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
            return new CapitalBaselineReading(null, CapitalBaselineReadOutcome.UnavailableNoRow, today, null, null);
        }

        var gapDays = today.DayNumber - row.TradingDay.DayNumber;

        CapitalBaselineReading Unavailable(CapitalBaselineReadOutcome outcome) =>
            new(null, outcome, today, row.TradingDay, row.EquityInBase);

        // 鮮度（IADR-0354 決定4）。🔴 #1423 M-3, IADR-0581 決定2: 下の「直前の暦日」の判定がより厳しく包むため、
        // この検査で新たに止まる場面は無い。残すのは帰結の区別のためだけである（巡回が長く死んでいたことを
        // 「直前の暦日の欠け」と分けて数える）。
        if (now - row.ObservedAtUtc > options.MaxAge)
        {
            return Unavailable(CapitalBaselineReadOutcome.UnavailableStale);
        }

        // 🔴 #869, IADR-0354 決定7: **0 以下の行は「判定できる値」として扱わない。**
        // 供給側（アダプタ）が既に 0 以下を弾いているが、**運用 Runbook が人手の 1 行投入を認めている**ため
        // 読み出し側にも同じ門を置く（本ガードより前に書かれた行にも効く）。
        // 0 を分母にすると比率上限がすべて 0 になり、平常状態でも `DailyLossLimitReached` が立ち、
        // 発注審査がその理由で**翌営業日まで続く日次損失ロックアウトを実際に張る**。
        if (row.EquityInBase <= 0m)
        {
            return Unavailable(CapitalBaselineReadOutcome.UnavailableNonPositive);
        }

        // 🔴 FR-10, #1423 M-3, ADR-0073 決定1・2, IADR-0581 決定1: **直前の米国東部暦日に成功した観測が無ければ
        // 「照会できない」として新規建てを拒否する**（旧: #889 / IADR-0372 決定A は Warning を出すだけで古い値を返した）。
        // 取引日は米国東部時間の暦日で数え、巡回（既定 5 分）は土日も回るため、期待される間隔は 1 日である
        // （IADR-0354 決定2）。それより開いていれば、直前の暦日の観測が 1 件も成功していない
        // ——口座照会が残高 0 を返した日・照会が壊れていた日・プロセスが落ちていた日のいずれかである。
        // 🔴 **原因は区別しない**（読み出し側から区別できないし、危険なのは原因ではなく
        // 「古い分母で統制が回る」という状態である）。**暦日で数える。営業日では数えない**（ADR-0073 決定1）。
        // 解除は自動である（直前の暦日の行が書かれれば、次の読み出しから供給に戻る。ADR-0073 決定2）。
        if (gapDays > 1)
        {
            return Unavailable(CapitalBaselineReadOutcome.UnavailableMissingPreviousDay);
        }

        return new CapitalBaselineReading(
            new CapitalBaseline(row.EquityInBase, row.TradingDay, row.ObservedAtUtc),
            CapitalBaselineReadOutcome.Supplied,
            today,
            row.TradingDay,
            row.EquityInBase);
    }
}
