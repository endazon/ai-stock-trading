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

    /// <summary>
    /// NFR（費用）, FR-04, #1523, IADR-0607 決定4: キャッシュの書き・読みをそれぞれの単価で数える。
    /// 金額 = 残り×入力 ＋ 書き×書き単価 ＋ 読み×読み単価 ＋ 出力×出力（残り = 全体 − 書き − 読み）。
    /// <paramref name="inputTokens"/> は**プロンプト全体**（ゲートウェイの InputTokens。書き・読みを含む）。
    /// 負のトークンは 0 とみなし、書き＋読みが全体を超える申告は残りを 0 とする（書き・読みは申告どおり数える＝過小にしない）。
    /// 段（プロンプト長の 2 段）は呼び出し側が全体の入力トークン数で選んだ <paramref name="price"/> を渡す。
    /// </summary>
    public static decimal Compute(
        int inputTokens, int cacheCreationInputTokens, int cacheReadInputTokens, int outputTokens, LlmPrice price)
    {
        var input = inputTokens > 0 ? inputTokens : 0;
        var write = cacheCreationInputTokens > 0 ? cacheCreationInputTokens : 0;
        var read = cacheReadInputTokens > 0 ? cacheReadInputTokens : 0;
        var output = outputTokens > 0 ? outputTokens : 0;
        var rest = Math.Max(0, input - write - read);
        return (rest / 1000m * price.InputPer1kTokens)
            + (write / 1000m * price.EffectiveCacheWritePer1kTokens)
            + (read / 1000m * price.EffectiveCacheReadPer1kTokens)
            + (output / 1000m * price.OutputPer1kTokens);
    }
}
