namespace AiStockTrading.Backtest.Domain;

// FR-15, ADR-0008, 06_daytrading-review §3.2, IADR-0038: LLM 学習カットオフ後データの検証（検証条件①）。
// バックテストの全バーが LLM の学習カットオフより後であれば、LLM の記憶による汚染がないと判定する。
public static class DataCutoffPolicy
{
    // 全バーの日付が cutoff より後（>）か。空は true（違反なし）。
    public static bool IsAllAfterCutoff(IReadOnlyList<PriceBar> bars, DateOnly cutoff)
    {
        ArgumentNullException.ThrowIfNull(bars);
        return bars.All(b => b.Date > cutoff);
    }

    // cutoff 以前（<=）のバー（汚染の疑い）を返す。
    public static IReadOnlyList<PriceBar> Violations(IReadOnlyList<PriceBar> bars, DateOnly cutoff)
    {
        ArgumentNullException.ThrowIfNull(bars);
        return bars.Where(b => b.Date <= cutoff).ToList();
    }
}
