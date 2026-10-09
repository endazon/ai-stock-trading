using AiStockTrading.Shared.Contracts.Llm;

namespace AiStockTrading.Shared.Contracts.Backtest;

// FR-04, FR-15, ADR-0011, ADR-0014 決定3, ADR-0017 決定2, ADR-0054 決定3, #1196, IADR-0498:
// **記録した判断が「両層の組」で採られたか**を判定する純関数（再生側が判定母集団を決めるために使う）。
//
// 計画 ADR-0054 決定3 は「Stage 0 は本番と同じ二段（スクリーニング → 本判断）を通した判断を評価し、実弾解禁の必須ゲートは
// 両層の組での通過である」と定めた。したがって 1 つの判断が合否に入るのは、
// **その判断に関わった全呼び出しが用途ごとのピン（`LlmAssignments`）どおりのモデルに答えられた**ときに限る。
// 組は利用者裁定 2026-10-10（planning#783）で `claude-haiku-5-5` ＋ `claude-sonnet-5-5` へ改めた（#1295, IADR-0524。
// 旧組は `claude-haiku-4-5` ＋ `claude-sonnet-5`）。
//
// 🔴 #1295, IADR-0524: 割当表は移行期間に限り直前世代も「割当どおり」として受ける（取引判断を止めないため）が、
// **本判定は受けない**（`LlmAssignmentEvaluation.MatchesCurrentPin`。`Allowed` を使わない）。旧組の記録で合格しても
// 5.5 系の組の合格にならない —— どちらの層のモデルを変えても Stage 0 は再実施する（ADR-0011 / ADR-0014 決定3 / ADR-0054 決定3）。
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
    /// 移行期間に受ける直前世代（#1295）は一致と読まない。
    /// </summary>
    public static bool MatchesPinnedAssignments(Stage0DecisionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Screening is not { } screening)
            return false;

        if (!LlmAssignmentEvaluator.Evaluate(LlmPurposes.TradeDecisionScreening, screening.EffectiveModelId).MatchesCurrentPin)
            return false;

        return (record.RawDecisions ?? [])
            .All(raw => LlmAssignmentEvaluator.Evaluate(LlmPurposes.TradeDecision, raw.EffectiveModelId).MatchesCurrentPin);
    }
}
