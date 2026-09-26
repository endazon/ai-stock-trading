namespace InformationCollectionService.Domain;

// FR-01, ADR-0031 決定4（計画 ADR-0043 決定2 (b) が部分改定）, #1015, IADR-0435: 1 巡回の Finnhub の要求が巡回間隔に収まる銘柄数。
//
// 計画 ADR-0043 決定2 (b): **1 巡回の要求数 ÷ そのプロセスの自制レート（回/分）が、巡回間隔（分）を超えないこと。**
// 1 巡回の要求数＝銘柄数 × 1 銘柄あたりの要求数。したがって収まる銘柄数は floor(自制レート × 巡回間隔 ÷ 1 銘柄あたりの要求数)。
//
// 🔴 **端数は切り捨てる**（収まらない側へ倒さない）。自制レート自体はトークンバケットが守るので、ここで決めるのは
// 「1 巡回で何銘柄まで問い合わせるか」だけである（超えると巡回が間隔を超えて伸び、1 銘柄あたりの確認が遅れる）。
public static class FinnhubCycleFit
{
    /// <summary>1 巡回で問い合わせてよい銘柄数の上限。</summary>
    /// <param name="ratePerMinute">自制レート（回/分）。1 未満は 1 として扱う（バケットの下限と同じ）。</param>
    /// <param name="intervalSeconds">巡回間隔（秒）。1 未満は 1 として扱う（ポーラの下限と同じ）。</param>
    /// <param name="requestsPerSymbol">1 銘柄あたりの要求数（現在値・企業ニュースの有効数）。1 以上。</param>
    public static int MaxSymbolsPerCycle(int ratePerMinute, int intervalSeconds, int requestsPerSymbol)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestsPerSymbol);

        var rate = (long)Math.Max(1, ratePerMinute);
        var interval = (long)Math.Max(1, intervalSeconds);
        var fits = rate * interval / (60L * requestsPerSymbol);
        return fits > int.MaxValue ? int.MaxValue : (int)fits;
    }
}
