namespace AiStockTrading.Shared.Infrastructure.Composable.Llm;

// NFR（費用）, FR-04, IADR-0055 決定2, IADR-0121 決定2: 1 モデル分の単価（**円 / 1,000 トークン**）。
// 金額の単位が円である点は LlmPricing.Compute と月次上限（¥15,000）に合わせている。
public readonly record struct LlmPrice(decimal InputPer1kTokens, decimal OutputPer1kTokens)
{
    /// <summary>単価未設定。fail-safe の最終段＝計上しても統制判定に影響しない（IADR-0055 の安全既定）。</summary>
    public static readonly LlmPrice Zero = new(0m, 0m);
}
