namespace TradeDecisionService.Features.TradeDecision.DecideTrade;

// FR-10, FR-04, FR-02, #1104, IADR-0460 決定3: 新規建ての損切り幅の観測値（ログ専用。統制ではない）。
// 損切り幅は LLM の出力（stopLossDistancePerShare）をそのまま使い、ATR は計算していない（計画は ATR 連動を定める。
// 数値の下限は planning#703 の裁定待ち）。幅が当日の値動きに比べて狭すぎないかを、運用ログから後で確かめられるようにする。
// 🔴 **不明は null**（0 にしない）。日中の値幅は高値・安値の両方が分かり、高値 ＞ 安値のときだけ持つ（0 除算をしない）。
public sealed record StopWidthObservation(
    decimal LlmReferencePrice,
    decimal AnchoredPrice,
    decimal AnchorDifference,
    decimal WidthPerShare,
    decimal WidthPercentOfAnchored,
    decimal? IntradayRange,
    decimal? WidthToIntradayRange)
{
    /// <summary>比率・倍率の小数桁（四捨五入）。</summary>
    public const int Decimals = 4;

    /// <summary>
    /// 観測値を計算する。<paramref name="anchoredPrice"/> は発注に用いる参照価格（現在値へアンカリング済み・正）。
    /// 差はアンカリング済み − LLM の参照価格、比率はアンカリング済みの価格に対する % である。
    /// </summary>
    public static StopWidthObservation Of(
        decimal llmReferencePrice, decimal anchoredPrice, decimal widthPerShare, IntradayPriceContext? intraday)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(anchoredPrice);

        decimal? range = intraday is { High: { } high, Low: { } low } && high > low ? high - low : null;

        return new StopWidthObservation(
            llmReferencePrice,
            anchoredPrice,
            anchoredPrice - llmReferencePrice,
            widthPerShare,
            Round(widthPerShare / anchoredPrice * 100m),
            range,
            range is { } r ? Round(widthPerShare / r) : null);
    }

    private static decimal Round(decimal value) => Math.Round(value, Decimals, MidpointRounding.AwayFromZero);
}
