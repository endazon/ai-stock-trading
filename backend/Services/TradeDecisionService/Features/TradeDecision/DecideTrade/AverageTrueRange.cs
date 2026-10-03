extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;

namespace TradeDecisionService.Features.TradeDecision.DecideTrade;

// 🔴 FR-10, ADR-0049 決定2, ADR-0003, #1122, IADR-0486 決定3: ATR(14, 日足) を求める純関数（数値はコードで計算する。LLM に計算させない）。
//
//   True Range ＝ max(当日高値 − 当日安値, |当日高値 − 前日終値|, |当日安値 − 前日終値|)
//   ATR(14)    ＝ 判断時点の前営業日までの確定足で、直近 14 本の True Range の単純平均（足は 15 本要る＝最古の 1 本は前日終値にだけ使う）
//
// 🔴 **得られないときは null**（呼び出し側は参照価格の 2% へ退避する。0 として持たない）:
//   - 足が無い・最後の足が期待する前営業日でない（古い足・公開の遅れ・臨時休場の翌日）。
//   - 足が 15 本に満たない。
//   - 窓の 15 本に壊れた足がある（高値 ＜ 安値・高値 / 安値 / 終値が 0 以下）。
//   - ATR が 0 以下（値動きが無い＝下限として意味を持たない）。
// 🔴 **当日の未確定の足は入ってこない**（CachedDailyBarsProvider.Confirm が取引日以降の足を捨てる）。ここでも最後の足が前営業日であることを確かめる。
// 🔴 **分割**: 足は前復権（RehabType_Forward。#1117 の実測で価格も出来高も分割に合わせて調整される）を 1 回の取得で揃えたものに限る。
// 取得をまたいで継ぎ足さない（CachedDailyBarsProvider は取引日ごとに全部を取り直す）。この関数は分割を補正しない。
// 端数は丸めない。
public static class AverageTrueRange
{
    /// <summary>ATR の本数（<see cref="TradingDefaults.StopWidthFloorAtrPeriod"/>＝14）。</summary>
    public static int Period => TradingDefaults.StopWidthFloorAtrPeriod;

    /// <summary>必要な足の本数（本数＋前日終値の 1 本＝15）。</summary>
    public static int RequiredBars => Period + 1;

    /// <summary>前営業日までの確定足から ATR(14) を求める。得られなければ null。</summary>
    public static decimal? Compute(ConfirmedDailyBars? bars)
    {
        if (bars is not { Bars.Count: > 0 })
            return null;

        if (bars.Bars[^1].Date != bars.ExpectedPreviousTradingDay)
            return null;

        if (bars.Bars.Count < RequiredBars)
            return null;

        var window = bars.Bars.Skip(bars.Bars.Count - RequiredBars).ToList();
        if (window.Any(b => b.High < b.Low || b.High <= 0m || b.Low <= 0m || b.Close <= 0m))
            return null;

        var sum = 0m;
        for (var i = 1; i < window.Count; i++)
            sum += TrueRange(window[i], window[i - 1].Close);

        var atr = sum / Period;
        return atr > 0m ? atr : null;
    }

    /// <summary>True Range ＝ max(高値 − 安値, |高値 − 前日終値|, |安値 − 前日終値|)。</summary>
    public static decimal TrueRange(DailyBar bar, decimal previousClose) =>
        Math.Max(bar.High - bar.Low, Math.Max(Math.Abs(bar.High - previousClose), Math.Abs(bar.Low - previousClose)));
}
