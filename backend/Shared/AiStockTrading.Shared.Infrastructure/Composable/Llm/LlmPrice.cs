namespace AiStockTrading.Shared.Infrastructure.Composable.Llm;

// NFR（費用）, FR-04, IADR-0055 決定2, IADR-0122 決定2: 1 モデル分の単価（**円 / 1,000 トークン**）。
// 金額の単位が円である点は LlmPricing.Compute と月次上限（¥15,000）に合わせている。
// NFR（費用）, #1523, IADR-0607 決定5/6: プロンプトキャッシュの書き・読みの単価（任意）。無い（null）ときは
// 書き＝入力×1.25（提供元の割増の比）・読み＝入力（従前の計上と同じ）で数える＝過小にしない側。
public readonly record struct LlmPrice(
    decimal InputPer1kTokens,
    decimal OutputPer1kTokens,
    decimal? CacheWritePer1kTokens = null,
    decimal? CacheReadPer1kTokens = null)
{
    /// <summary>書きの単価が無いときの、入力単価に対する倍率（提供元の 5 分のキャッシュの書きの割増）。</summary>
    public const decimal DerivedCacheWriteMultiplier = 1.25m;

    /// <summary>単価未設定。fail-safe の最終段＝計上しても統制判定に影響しない（IADR-0055 の安全既定）。</summary>
    public static readonly LlmPrice Zero = new(0m, 0m);

    /// <summary>キャッシュへ書いた入力トークンの単価（無ければ入力×1.25）。</summary>
    public decimal EffectiveCacheWritePer1kTokens => CacheWritePer1kTokens ?? InputPer1kTokens * DerivedCacheWriteMultiplier;

    /// <summary>キャッシュから読んだ入力トークンの単価（無ければ入力単価＝従前の計上と同じ）。</summary>
    public decimal EffectiveCacheReadPer1kTokens => CacheReadPer1kTokens ?? InputPer1kTokens;
}
