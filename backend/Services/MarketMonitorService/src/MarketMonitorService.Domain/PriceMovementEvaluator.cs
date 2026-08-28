namespace MarketMonitorService.Domain;

// FR-03: 価格変動の判定結果。変動率と閾値超過フラグ。
public readonly record struct PriceMovement(decimal ChangeRatio, bool Exceeded);

// FR-03, UC-02: 変動率が閾値を超えたかを決定的に判定する。基準値は「前回 AI 判断時点の価格」。
public static class PriceMovementEvaluator
{
    // 変動率 = (現在値 − 基準値) / 基準値。|変動率| ≥ 閾値 で超過。
    // 基準値が 0 以下（未確定・異常）の場合は判定不能として超過扱いにしない（誤発火を避ける）。
    public static PriceMovement Evaluate(decimal currentPrice, decimal baselinePrice, decimal thresholdRatio)
    {
        if (baselinePrice <= 0m)
        {
            return new PriceMovement(0m, false);
        }

        var changeRatio = (currentPrice - baselinePrice) / baselinePrice;
        var exceeded = Math.Abs(changeRatio) >= thresholdRatio;
        return new PriceMovement(changeRatio, exceeded);
    }
}
