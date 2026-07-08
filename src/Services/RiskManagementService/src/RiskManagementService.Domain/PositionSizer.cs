namespace AiStockTrading.RiskManagement.Domain;

// FR-10: 1取引あたりリスクに基づくポジションサイジング（ATR連動を想定した損切り幅入力）
public static class PositionSizer
{
    /// <summary>
    /// リスク予算（資金 × 1取引リスク × 縮小係数）を損切り幅で割って株数を算出する。
    /// 損切り幅が正でない場合は 0（見送り）を返す。
    /// </summary>
    public static int CalculateQuantity(
        decimal capital,
        decimal perTradeRiskRatio,
        decimal stopLossDistancePerShare,
        decimal sizeFactor = 1m)
    {
        if (stopLossDistancePerShare <= 0m)
        {
            return 0;
        }

        var riskBudget = capital * perTradeRiskRatio * sizeFactor;
        return (int)Math.Floor(riskBudget / stopLossDistancePerShare);
    }

    /// <summary>
    /// 連敗・ドローダウンに応じたサイズ縮小係数を返す（裁量で戻さない機械的ルール）。
    /// - 連敗がしきい値以上: 縮小係数（既定 0.5）を乗算
    /// - ドローダウンが上限の 1/2 以上: 0.5 を乗算（DD が深まるほど縮小する決定的ルール）
    /// </summary>
    public static decimal GetSizeFactor(
        int consecutiveLosses,
        decimal drawdownRatio,
        RiskLimitSettings limits)
    {
        var factor = 1m;

        if (consecutiveLosses >= limits.LosingStreakThreshold)
        {
            factor *= limits.LosingStreakSizeFactor;
        }

        if (drawdownRatio >= limits.MaxDrawdownRatio / 2m)
        {
            factor *= 0.5m;
        }

        return factor;
    }
}
