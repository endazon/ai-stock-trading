using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.TradeDecision.Domain;

// FR-17, 05_trading-assumptions §4, IADR-0076: 採算評価の判定結果。
// Viable=採算成立（発注可）／NotViable=採算不成立（見送り）／Indeterminate=費用見積り不能（安全側で見送り）。
public enum ProfitabilityVerdict
{
    Viable,
    NotViable,
    Indeterminate,
}

// FR-17, 05_trading-assumptions §4, IADR-0076: 概算費用関数による採算評価（純関数）。
// 最小期待利益しきい値 = **(往復費用 + 判断費用) と税の合計**の最小期待利益倍率（#358・IADR-0173）。
// 税は譲渡益（= 利益 − 費用）に掛かるため結果に依存し、しきい値は不動点として解く。式の単一情報源は
// Shared.Contracts の MinimumExpectedProfit（設定サービスの CostCalculator と共有する）。
// 旧実装は「往復費用のみ × 1.5」であり、計画が 2026-07-23 に確定した基準へ追随していなかった。
// 費用の算出（手数料・スプレッド）は設定サービスの CostCalculator（IADR-0021）で行い、本ゲートは算出済みの数値を受ける
// （Domain を設定サービスに依存させない）。数値計算はコードで行い LLM には計算させない（05 採用方針）。
//
// fail-safe（IADR-0076 決定3）: 費用が見積れないほど安全側（Indeterminate → 呼び出し側 Hold）に倒す。
// 往復費用が未解決（null）・非正（実額未登録なら手数料 0）・倍率が非正（構成異常）は採算不能とみなす。
// 費用 0 でしきい値 0＝全通過を許さないのが本ガードレールの要点（実額登録で初めて有意に働く）。
public static class ProfitabilityGate
{
    /// <summary>
    /// 採算を評価する。<paramref name="estimatedRoundTripCost"/> が null（前提条件未解決）・非正、または
    /// <paramref name="minimumProfitMultiple"/> が非正のときは Indeterminate（安全側）。
    /// <paramref name="decisionCost"/> の負値は 0 に正規化する。
    /// </summary>
    /// <param name="expectedGrossProfit">想定利益（費用控除前・円）。LLM 判断由来（想定値幅 × 数量）。</param>
    /// <param name="estimatedRoundTripCost">往復の概算取引費用（円）。未解決なら null。</param>
    /// <param name="decisionCost">この取引の判断費用の見積り（円・任意項・既定 0）。</param>
    /// <param name="minimumProfitMultiple">最小期待利益倍率（計画確定値 2・前提条件由来）。</param>
    /// <param name="capitalGainsTaxRate">
    /// 譲渡益税率（計画確定値 0.20315・前提条件由来）。**しきい値の基準に税を含めるために要る**（#358）。
    /// 0 を渡すと従来式（費用 × 倍率）へ退化する。
    /// </param>
    public static ProfitabilityVerdict Evaluate(
        decimal expectedGrossProfit,
        decimal? estimatedRoundTripCost,
        decimal decisionCost,
        decimal minimumProfitMultiple,
        decimal capitalGainsTaxRate = 0m)
    {
        // 費用見積り不能・構成異常は安全側（Indeterminate）に倒す。
        if (estimatedRoundTripCost is not { } roundTrip || roundTrip <= 0m || minimumProfitMultiple <= 0m)
        {
            return ProfitabilityVerdict.Indeterminate;
        }

        var normalizedDecisionCost = decisionCost > 0m ? decisionCost : 0m;

        // **倍率 × 税率 >= 1 では解が無い**（利益を増やすと税も同じ速さで増える）。
        // 負のしきい値で全通過させないよう、構成異常として安全側へ倒す（#358・IADR-0173 決定2）。
        if (MinimumExpectedProfit.Threshold(
                roundTrip + normalizedDecisionCost, minimumProfitMultiple, capitalGainsTaxRate)
            is not { } minimumExpectedProfit)
        {
            return ProfitabilityVerdict.Indeterminate;
        }

        return expectedGrossProfit >= minimumExpectedProfit
            ? ProfitabilityVerdict.Viable
            : ProfitabilityVerdict.NotViable;
    }
}
