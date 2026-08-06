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
    /// リスク予算ベースの株数を、1 注文金額上限・利用可能資金でキャップした株数を返す（FR-10, IADR-0003）。
    /// 損切り幅が浅いと <see cref="CalculateQuantity"/> の株数は想定金額が金額上限を系統的に超過し、
    /// <see cref="RiskEvaluator"/> で必ず拒否される（サイジング→拒否のループ。Issue #29）。呼び出し元は
    /// 発注意図の数量確定にこのメソッドを用い、リスク予算基準と金額上限基準の小さい方を採る。
    /// </summary>
    /// <param name="referencePrice">1 株あたり参照価格（基準通貨・USD）。0 以下は見送り（0 株）。</param>
    /// <param name="maxOrderAmount">1 注文あたり金額上限（基準通貨・USD）。</param>
    /// <param name="availableCapital">この注文に投入可能な資金（基準通貨・USD）。段階資金上限の残枠など。</param>
    public static int CalculateCappedQuantity(
        decimal capital,
        decimal perTradeRiskRatio,
        decimal stopLossDistancePerShare,
        decimal referencePrice,
        decimal maxOrderAmount,
        decimal availableCapital,
        decimal sizeFactor = 1m)
    {
        if (referencePrice <= 0m)
        {
            return 0;
        }

        var riskBasedQuantity = CalculateQuantity(capital, perTradeRiskRatio, stopLossDistancePerShare, sizeFactor);

        // 想定金額が 1 注文金額上限・利用可能資金のいずれも超えないよう、金額基準の株数でキャップする。
        var amountCap = Math.Min(maxOrderAmount, availableCapital);
        var amountBasedQuantity = amountCap <= 0m ? 0 : (int)Math.Floor(amountCap / referencePrice);

        return Math.Min(riskBasedQuantity, amountBasedQuantity);
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
