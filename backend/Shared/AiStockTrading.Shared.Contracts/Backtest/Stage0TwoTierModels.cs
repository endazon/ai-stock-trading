using AiStockTrading.Shared.Contracts.Llm;

namespace AiStockTrading.Shared.Contracts.Backtest;

// FR-04, FR-15, ADR-0011, ADR-0014 決定3, ADR-0017 決定2, ADR-0054 決定3, #1196, IADR-0498:
// **記録した判断が「両層の組」で採られたか**を判定する純関数（再生側が判定母集団を決めるために使う）。
//
// 計画 ADR-0054 決定3 は「Stage 0 は本番と同じ二段（スクリーニング → 本判断）を通した判断を評価し、実弾解禁の必須ゲートは
// 両層の組（`claude-haiku-4-5` ＋ `claude-sonnet-5`）での通過である」と定めた。したがって 1 つの判断が合否に入るのは、
// **その判断に関わった全呼び出しが用途ごとのピン（`LlmAssignments`）どおりのモデルに答えられた**ときに限る。
//
// 🔴 照合の基準は構成の希望値（`Stage0Recording:Model`）ではなく `LlmAssignmentEvaluator` である —— 希望値と照合すると、
// 希望値ごと別モデルへ向けた記録が「一致」と読めてしまう。**不明（null）は一致と読まない。**
public static class Stage0TwoTierModels
{
    /// <summary>一次スクリーニングを記録しているか（false は二段化より前の記録＝評価不能）。</summary>
    public static bool IsScreeningRecorded(Stage0DecisionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.Screening is not null;
    }

    /// <summary>
    /// 一次の実効モデルが `trade-decision-screening` のピン、**かつ**二次の全票の実効モデルが `trade-decision` のピンと一致するか。
    /// 一次を記録していない記録は false（一致を確かめられない）。一次で見送った判断（票 0）は一次だけを見る。
    /// </summary>
    public static bool MatchesPinnedAssignments(Stage0DecisionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Screening is not { } screening)
            return false;

        if (!LlmAssignmentEvaluator.Evaluate(LlmPurposes.TradeDecisionScreening, screening.EffectiveModelId).Allowed)
            return false;

        return (record.RawDecisions ?? [])
            .All(raw => LlmAssignmentEvaluator.Evaluate(LlmPurposes.TradeDecision, raw.EffectiveModelId).Allowed);
    }
}
