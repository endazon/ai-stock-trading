using System.Globalization;
using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.CostControl.Application.State;
using AiStockTrading.CostControl.Domain;

namespace AiStockTrading.CostControl.Application.Services;

// NFR（費用）, 05_trading-assumptions §6, IADR-0027: 費用の月次計上と LLM 上限に対する統制判定。
// LLM の月内累計が上限の 80% で間隔延長・100% で停止。状態が上方に遷移したときのみ CrossedTo を返す（イベント発行対象）。
public sealed class CostControlService(ICostLedger ledger, ICostLimitsProvider limitsProvider, IClock clock)
{
    // 費用計上月（UTC の年月）。月をまたぐと累計はリセットされる（GetMonthlyTotal が月で絞るため）。
    public static string MonthKey(DateTimeOffset instant) => instant.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public RecordCostResult Record(CostCategory category, decimal amount)
    {
        var now = clock.UtcNow;
        var month = MonthKey(now);
        var limits = limitsProvider.GetLimits();

        // LLM は計上前後の状態を比較し、上方遷移を検知する（他カテゴリは計上のみ）。
        var beforeState = CostGovernor.EvaluateLlm(ledger.GetMonthlyTotal(month, CostCategory.Llm), limits).State;
        ledger.Record(month, category, amount, now);
        var llmTotal = ledger.GetMonthlyTotal(month, CostCategory.Llm);
        var decision = CostGovernor.EvaluateLlm(llmTotal, limits);

        var crossedTo = decision.State > beforeState ? decision.State : (CostControlState?)null;
        var percent = limits.Llm <= 0m ? 0m : llmTotal / limits.Llm * 100m;

        return new RecordCostResult(decision, crossedTo, percent, month);
    }

    /// <summary>現在月の LLM 統制判定。</summary>
    public CostControlDecision GetLlmState()
    {
        var month = MonthKey(clock.UtcNow);
        return CostGovernor.EvaluateLlm(ledger.GetMonthlyTotal(month, CostCategory.Llm), limitsProvider.GetLimits());
    }

    /// <summary>現在月の費用÷資金比率（月報の費用レビュー・FR-16）。</summary>
    public decimal Review(decimal capital)
    {
        var month = MonthKey(clock.UtcNow);
        return CostReview.CostToCapitalRatio(ledger.GetMonthlyTotalAll(month), capital);
    }
}
