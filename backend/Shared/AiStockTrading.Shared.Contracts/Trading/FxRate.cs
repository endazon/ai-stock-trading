namespace AiStockTrading.Shared.Contracts.Trading;

// FR-10, FR-17, #257, IADR-0106: 為替レートの観測。Rate は「Quote 通貨 1 単位あたりの Base 通貨額」。
// AsOf は観測時点（FRED の日次系列なら観測日）で、鮮度判定に用いる（古い観測で発注しないため）。
public sealed record FxRate(Currency Quote, Currency Base, decimal Rate, DateTimeOffset AsOf)
{
    /// <summary>
    /// 同一通貨の恒等レート（1）。観測ではなく定義であるため陳腐化しない（AsOf は
    /// <see cref="DateTimeOffset.MaxValue"/>＝鮮度判定の対象外を表す）。
    /// </summary>
    public static FxRate Identity(Currency currency) => new(currency, currency, 1m, DateTimeOffset.MaxValue);
}
