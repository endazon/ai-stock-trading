namespace InformationCollectionService.Domain;

// FR-01, ADR-0020 §結果（フォローアップ）: Finnhub Free の**日次上限から監視銘柄数の上限を逆算**する純関数。
//
// 🔴 **日次上限はまだ実測していない。** 計画は第三者検証の観測（2026 年 4 月時点で 1 日およそ 300 回）を
// 注記として持つが、**それは実測値ではない**。IADR-0275 で分次の上限（60 回/60 秒固定ウィンドウ）は
// 実測確認済みだが、**日次上限は実測セッションでも確定できなかった**（持続的ブロックを観測できず）。
// **上限を設定値として受け取り、未設定（null）なら銘柄数上限を返さない**という本実装の方針は維持する。
//
// **推測値を既定値として焼き込まない。** 焼き込むと「実測した上限」として運用に伝わり、
// 上限超過（429・一時ブロック）を「実測どおりのはずなのに起きた事象」として扱うことになる。
public static class FinnhubQuotaCalculator
{
    /// <summary>
    /// 日次上限から監視銘柄数の上限を逆算する。
    /// </summary>
    /// <param name="dailyRequestLimit">1 日あたりのリクエスト上限。<b>null＝未実測</b>。</param>
    /// <param name="cyclesPerDay">1 日の収集巡回回数（開場中 30 分毎なら 13 回程度）。</param>
    /// <param name="requestsPerSymbolPerCycle">1 巡回・1 銘柄あたりのリクエスト数（現在値 1 ＋ 企業ニュース 1 ＝ 2）。</param>
    /// <param name="reservedRequests">再試行・臨時照会のために空けておく余裕。</param>
    /// <returns>監視できる銘柄数の上限。<b>上限が未実測なら null</b>。</returns>
    public static int? MaxWatchlistSymbols(
        int? dailyRequestLimit,
        int cyclesPerDay,
        int requestsPerSymbolPerCycle,
        int reservedRequests = 0)
    {
        if (dailyRequestLimit is not { } limit)
            return null;

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cyclesPerDay);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestsPerSymbolPerCycle);
        ArgumentOutOfRangeException.ThrowIfNegative(reservedRequests);

        var budget = limit - reservedRequests;
        if (budget <= 0)
            return 0;

        // 端数は切り捨てる。**足りない側へ倒す**——超過はブロックを招き、収集が丸ごと止まる。
        return budget / (cyclesPerDay * requestsPerSymbolPerCycle);
    }
}
