using AiStockTrading.Shared.Kernel.Trading;

namespace CostControlService.Domain;

// NFR（費用）, 05_trading-assumptions §6, IADR-0027: 費用のカテゴリ。
//
// NFR（費用）, 05_trading-assumptions §6.1, #347, IADR-0218: **Llm は「月次上限の対象となる LLM 費用」である。**
// 対象は取引判断サイクルのみであり、報告書生成・情報収集の LLM 費用は LlmUncapped へ分ける。
// 同じカウンタに積むと、100% 到達で報告書生成が止まり、日報が確定せず翌営業日の取引が止まる連鎖が生じる
// （UC-01 の事前条件）。**カテゴリの分離がこの連鎖を構造的に断つ唯一の手段である。**
public enum CostCategory
{
    Llm,
    Infrastructure,
    Data,

    // 🔴 **末尾に足すこと。** 永続化された値（CostEntryRow.Category）は序数で保存されており、
    // 途中に挿入すると既存行の意味が変わる（7 年保持の台帳を遡って壊す）。
    /// <summary>
    /// 月次 LLM 費用上限の**対象外**の LLM 費用（報告書生成・情報収集）。抑制動作は行わず、
    /// 実績として記録し月報へ供給する（05_trading-assumptions §6.1 の表・#282 の過少申告の再発防止）。
    /// </summary>
    LlmUncapped,
}

// 費用統制の状態。Normal=通常 / Throttled=間隔延長（80%到達）/ Halted=停止（100%到達）。
public enum CostControlState
{
    Normal,
    Throttled,
    Halted,
}

// 統制判定。IntervalMultiplier は定時サイクル間隔の倍率（Normal=1・Throttled=2）。Halted は停止で 0（無効値）＝サイクルを回さない。
// 呼び出し側は IsHalted を先に見ること（IntervalMultiplier だけで判断しない）。
public sealed record CostControlDecision(CostControlState State, decimal IntervalMultiplier)
{
    public bool IsHalted => State == CostControlState.Halted;
}

// NFR（費用）, 05_trading-assumptions §6, IADR-0027: 月内 LLM 累計費用と上限から統制判定を返す純関数。
// ratio = 累計 / LLM月次上限。80%到達で間隔延長（倍率2）、100%到達で停止。
public static class CostGovernor
{
    // 05 §6: 80% で間隔延長、100% で停止。
    public const decimal ThrottleThreshold = 0.80m;
    public const decimal HaltThreshold = 1.00m;
    public const decimal ThrottledIntervalMultiplier = 2m;

    public static CostControlDecision EvaluateLlm(decimal monthlyLlmCost, MonthlyCostLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        // 上限が 0（未設定）なら統制しない（Normal）。負の費用は 0 とみなす。
        if (limits.Llm <= 0m)
            return new CostControlDecision(CostControlState.Normal, 1m);

        var ratio = Math.Max(0m, monthlyLlmCost) / limits.Llm;

        if (ratio >= HaltThreshold)
            return new CostControlDecision(CostControlState.Halted, 0m); // 停止＝間隔倍率 0（無効値・IsHalted を見ること）
        if (ratio >= ThrottleThreshold)
            return new CostControlDecision(CostControlState.Throttled, ThrottledIntervalMultiplier);
        return new CostControlDecision(CostControlState.Normal, 1m);
    }
}
