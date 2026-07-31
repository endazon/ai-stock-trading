namespace AiStockTrading.Shared.Infrastructure.Composable.Llm;

// NFR（費用）, FR-04, IADR-0055 決定2: LLM 費用の単価適用（純関数）。
// 費用 = 入力トークン÷1000×入力単価 + 出力トークン÷1000×出力単価（いずれも円）。
// fail-safe: 単価未設定（0）なら 0 円＝計上しても統制判定に影響しない（安全既定）。
// IADR-0122 決定2: 単価は用途別モデル割当に追随して LlmPriceTable が解決する。#282（report-service の計上経路）
// からも同じ単価で計上できるよう、TradeDecisionService.Domain から本共有プロジェクトへ移設した
// （サービス間の直接参照は禁止のため、取引判断サービスに置いたままでは二重定義になる）。
public static class LlmPricing
{
    /// <summary>トークン数と 1k トークン単価（円）から費用（円）を算出する。負のトークンは 0 とみなす。</summary>
    public static decimal Compute(int inputTokens, int outputTokens, decimal inputPer1kTokens, decimal outputPer1kTokens)
    {
        var input = inputTokens > 0 ? inputTokens : 0;
        var output = outputTokens > 0 ? outputTokens : 0;
        return (input / 1000m * inputPer1kTokens) + (output / 1000m * outputPer1kTokens);
    }

    /// <summary>解決済みの単価（<see cref="LlmPriceTable.Resolve"/> の結果）を適用する。</summary>
    public static decimal Compute(int inputTokens, int outputTokens, LlmPrice price) =>
        Compute(inputTokens, outputTokens, price.InputPer1kTokens, price.OutputPer1kTokens);
}
