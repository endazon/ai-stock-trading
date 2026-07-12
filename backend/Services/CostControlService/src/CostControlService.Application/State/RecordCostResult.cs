using AiStockTrading.CostControl.Domain;

namespace AiStockTrading.CostControl.Application.State;

// NFR（費用）: 費用計上の結果。CrossedTo は統制状態が上方へ遷移した場合の新状態（イベント発行対象・null なら遷移なし）。
// Percent は LLM 月内累計 ÷ LLM 上限（百分率）。
public sealed record RecordCostResult(
    CostControlDecision Decision,
    CostControlState? CrossedTo,
    decimal Percent,
    string Month);
