using System.Globalization;
using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.CostControl.Application.State;
using AiStockTrading.CostControl.Domain;

namespace AiStockTrading.CostControl.Application.Services;

// NFR（費用）, 05_trading-assumptions §6, IADR-0027: 費用の月次計上と LLM 上限に対する統制判定。
// LLM の月内累計が上限の 80% で間隔延長・100% で停止。状態が上方に遷移したときのみ CrossedTo を返す（イベント発行対象）。
//
// FR-17, IADR-0065: 上限は利用者が設定サービスで変更しうるため、都度 ICostLimitsProvider から解決する（非同期）。
public sealed class CostControlService(ICostLedger ledger, ICostLimitsProvider limitsProvider, IClock clock)
{
    // 費用計上月（UTC の年月）。月をまたぐと累計はリセットされる（GetMonthlyTotal が月で絞るため）。
    public static string MonthKey(DateTimeOffset instant) => instant.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public async ValueTask<RecordCostResult> RecordAsync(
        CostCategory category, decimal amount, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var month = MonthKey(now);

        // 上限の解決は台帳への追記より**前**に済ませる。追記後に解決すると、上限取得の待ち（キャッシュミス時の HTTP 往復）
        // が before/after 判定の間に挟まり、IADR-0034 の「遷移は高々 1 回」の窓を無用に広げてしまう。
        var limits = await limitsProvider.GetLimitsAsync(cancellationToken).ConfigureAwait(false);

        // IADR-0034: 追記と当該月 LLM 累計の before/after を原子的に得る（並行計上でもしきい値遷移を高々 1 回に保つ）。
        // 非 LLM 計上は LLM 累計を変えないため before==after となり crossedTo は自然に null になる。
        var outcome = ledger.Record(month, category, amount, now);

        var beforeState = CostGovernor.EvaluateLlm(outcome.LlmTotalBefore, limits).State;
        var decision = CostGovernor.EvaluateLlm(outcome.LlmTotalAfter, limits);
        var crossedTo = decision.State > beforeState ? decision.State : (CostControlState?)null;
        var percent = limits.Llm <= 0m ? 0m : outcome.LlmTotalAfter / limits.Llm * 100m;

        return new RecordCostResult(decision, crossedTo, percent, month);
    }

    /// <summary>現在月の LLM 統制判定。</summary>
    public async ValueTask<CostControlDecision> GetLlmStateAsync(CancellationToken cancellationToken = default)
    {
        var month = MonthKey(clock.UtcNow);
        var limits = await limitsProvider.GetLimitsAsync(cancellationToken).ConfigureAwait(false);
        return CostGovernor.EvaluateLlm(ledger.GetMonthlyTotal(month, CostCategory.Llm), limits);
    }

    /// <summary>現在月の費用÷資金比率（月報の費用レビュー・FR-16）。上限を参照しないため同期のまま。</summary>
    public decimal Review(decimal capital)
    {
        var month = MonthKey(clock.UtcNow);
        return CostReview.CostToCapitalRatio(ledger.GetMonthlyTotalAll(month), capital);
    }
}
