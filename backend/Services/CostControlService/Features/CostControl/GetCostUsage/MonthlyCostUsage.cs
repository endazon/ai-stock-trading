using CostControlService.Domain;

namespace CostControlService.Features.CostControl.GetCostUsage;

// NFR（費用）, FR-16, 05_trading-assumptions §6.1, #347, IADR-0218: 当月の費用実績（月報への供給形）。
//
// Totals は**カテゴリ別の内訳**であり、月次 LLM 費用上限の対象（`CostCategory.Llm`）と
// 対象外（`CostCategory.LlmUncapped`＝報告書生成・情報収集）を分けて持つ。
// GovernedPercent は**対象分だけ**を上限で割った消費率であり、対象外は分子に入らない
// （入れると「上限に近づいている」という誤った運用シグナルになる）。
// LlmLimit が 0（未設定）なら GovernedPercent は 0 とする。
public sealed record MonthlyCostUsage(
    string Month,
    IReadOnlyDictionary<CostCategory, decimal> Totals,
    decimal LlmLimit,
    decimal GovernedPercent);
