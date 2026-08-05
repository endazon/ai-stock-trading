using AiStockTrading.RiskManagement.Domain;

namespace AiStockTrading.Backtest.Domain;

// FR-15, FR-20, ADR-0008, IADR-0045: 段階昇格の推奨。実際の遷移・承認は FR-20 の利用者承認フロー（#20）。
public sealed record StagePromotionRecommendation(
    TradingStage FromStage,
    TradingStage? ToStage,
    bool Recommended,
    string Rationale);

// FR-15, FR-20: Stage 0 合格判定から Stage 1 への昇格推奨を導く（純関数）。自動昇格はしない（規律・#20）。
public static class Stage0Promotion
{
    public static StagePromotionRecommendation Evaluate(Stage0GateResult gate)
    {
        ArgumentNullException.ThrowIfNull(gate);

        if (gate.Passed)
        {
            return new StagePromotionRecommendation(
                TradingStage.Stage0Verification,
                TradingStage.Stage1Simulate,
                Recommended: true,
                Rationale: "Stage 0 合格基準を満たしたため Stage 1（SIMULATE）への昇格を推奨（承認は利用者・#20）。");
        }

        var reasons = gate.FormatFailedChecks();
        return new StagePromotionRecommendation(
            TradingStage.Stage0Verification,
            ToStage: null,
            Recommended: false,
            Rationale: $"Stage 0 不合格のため据え置き。未達条件: {reasons}。");
    }
}
