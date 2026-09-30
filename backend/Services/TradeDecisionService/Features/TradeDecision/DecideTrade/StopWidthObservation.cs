using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision.DecideTrade;

// FR-10, FR-04, FR-02, #1104, IADR-0460 決定3: 新規建ての損切り幅の観測値（ログ専用）。幅が当日の値動きに比べて
// 狭すぎないかを、運用ログから後で確かめられるようにする。
// #1120, ADR-0049, IADR-0465 決定3: 幅は LLM が提案し、系が下限（ATR(14)。得られない間は参照価格の 2%）を掛ける。
// **WidthPerShare・比率・倍率は AI の幅**のまま（AI の提案の傾向を測る）で、下限・出所・適用した幅・広げたかを並べる。
// 🔴 **不明は null**（0 にしない）。日中の値幅は高値・安値の両方が分かり、高値 ＞ 安値のときだけ持つ（0 除算をしない）。
public sealed record StopWidthObservation(
    decimal LlmReferencePrice,
    decimal AnchoredPrice,
    decimal AnchorDifference,
    decimal WidthPerShare,
    decimal WidthPercentOfAnchored,
    decimal? IntradayRange,
    decimal? WidthToIntradayRange,
    decimal FloorPerShare,
    StopWidthFloorSource FloorSource,
    decimal AppliedWidthPerShare,
    bool Widened)
{
    /// <summary>比率・倍率の小数桁（四捨五入）。</summary>
    public const int Decimals = 4;

    /// <summary>
    /// 観測値を計算する。<paramref name="anchoredPrice"/> は発注に用いる参照価格（現在値へアンカリング済み・正）。
    /// 差はアンカリング済み − LLM の参照価格、比率はアンカリング済みの価格に対する % である（いずれも AI の幅）。
    /// <paramref name="stopWidth"/> は下限を掛けた結果（IADR-0465）。
    /// </summary>
    public static StopWidthObservation Of(
        decimal llmReferencePrice, decimal anchoredPrice, decimal widthPerShare, IntradayPriceContext? intraday,
        StopWidthFloorApplication stopWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(anchoredPrice);
        ArgumentNullException.ThrowIfNull(stopWidth);

        decimal? range = intraday is { High: { } high, Low: { } low } && high > low ? high - low : null;

        return new StopWidthObservation(
            llmReferencePrice,
            anchoredPrice,
            anchoredPrice - llmReferencePrice,
            widthPerShare,
            Round(widthPerShare / anchoredPrice * 100m),
            range,
            range is { } r ? Round(widthPerShare / r) : null,
            stopWidth.FloorPerShare,
            stopWidth.FloorSource,
            stopWidth.AppliedWidthPerShare,
            stopWidth.Widened);
    }

    private static decimal Round(decimal value) => Math.Round(value, Decimals, MidpointRounding.AwayFromZero);
}
