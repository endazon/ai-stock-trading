namespace AiStockTrading.Backtest.Domain;

// FR-15, ADR-0008: バックテスト結果の集計指標。Stage 0 合格判定（DSR・最大DD 許容）の入力。
// DailyReturns は後段の過剰適合補正（DSR/PBO・Slice B）が消費する日次リターン列。
public sealed record BacktestMetrics(
    decimal TotalReturn,
    double SharpeRatio,
    decimal MaxDrawdown,
    decimal WinRate,
    int TradeCount,
    IReadOnlyList<decimal> EquityCurve,
    IReadOnlyList<double> DailyReturns);

public static class BacktestMetricsCalculator
{
    // 年率化係数（営業日 252 日）。
    public const double TradingDaysPerYear = 252d;

    public static BacktestMetrics Compute(IReadOnlyList<decimal> equityCurve, IReadOnlyList<decimal> tradePnls)
    {
        equityCurve ??= [];
        tradePnls ??= [];

        var totalReturn = TotalReturn(equityCurve);
        var dailyReturns = DailyReturns(equityCurve);
        var sharpe = AnnualizedSharpe(dailyReturns);
        var maxDrawdown = MaxDrawdown(equityCurve);
        var (winRate, tradeCount) = WinRate(tradePnls);

        return new BacktestMetrics(totalReturn, sharpe, maxDrawdown, winRate, tradeCount, equityCurve, dailyReturns);
    }

    // 総リターン = (最終エクイティ − 初期エクイティ) / 初期エクイティ。初期 0 以下は 0。
    private static decimal TotalReturn(IReadOnlyList<decimal> equity)
    {
        if (equity.Count < 2)
            return 0m;
        var first = equity[0];
        return first <= 0m ? 0m : (equity[^1] - first) / first;
    }

    // 日次リターン列 r_t = equity[t] / equity[t-1] − 1（前日 0 以下は 0）。
    private static IReadOnlyList<double> DailyReturns(IReadOnlyList<decimal> equity)
    {
        if (equity.Count < 2)
            return [];
        var returns = new double[equity.Count - 1];
        for (var i = 1; i < equity.Count; i++)
        {
            var prev = equity[i - 1];
            returns[i - 1] = prev <= 0m ? 0d : (double)((equity[i] - prev) / prev);
        }
        return returns;
    }

    // 年率化 Sharpe = (日次平均 / 日次標本標準偏差) × sqrt(252)。標本 2 未満・標準偏差 0 は 0（ゼロ除算回避）。
    private static double AnnualizedSharpe(IReadOnlyList<double> dailyReturns)
    {
        if (dailyReturns.Count < 2)
            return 0d;
        var mean = dailyReturns.Average();
        var variance = dailyReturns.Sum(r => (r - mean) * (r - mean)) / (dailyReturns.Count - 1);
        var std = Math.Sqrt(variance);
        return std == 0d ? 0d : mean / std * Math.Sqrt(TradingDaysPerYear);
    }

    // 最大ドローダウン = max_t (直近ピーク − equity[t]) / 直近ピーク。ピーク 0 以下の区間は寄与 0。
    private static decimal MaxDrawdown(IReadOnlyList<decimal> equity)
    {
        var peak = decimal.MinValue;
        var maxDd = 0m;
        foreach (var value in equity)
        {
            if (value > peak)
                peak = value;
            if (peak > 0m)
            {
                var dd = (peak - value) / peak;
                if (dd > maxDd)
                    maxDd = dd;
            }
        }
        return maxDd;
    }

    // 勝率 = 正の決済損益件数 / 総決済件数。取引 0 は 0。
    private static (decimal WinRate, int TradeCount) WinRate(IReadOnlyList<decimal> tradePnls)
    {
        if (tradePnls.Count == 0)
            return (0m, 0);
        var wins = tradePnls.Count(p => p > 0m);
        return ((decimal)wins / tradePnls.Count, tradePnls.Count);
    }
}
