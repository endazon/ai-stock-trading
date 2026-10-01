namespace AiStockTrading.Shared.Contracts.Trading;

/// <summary>
/// FR-10, ADR-0049 決定2, #1136, IADR-0472 決定7: 損切り幅の下限の<b>退避値の比率</b>の単一情報源（参照価格の 2%。05_trading-assumptions §5「損切り幅の下限」）。
/// <para>
/// 取引判断（新規建て。<c>TradingDefaults.StopWidthFloorFallbackRatio</c> はこの値を指す）と発注執行（既存の S1 の建玉への遡及）の両方が使う。
/// 発注執行はリスク管理を参照しないため、値の実体を共有契約に置く（<c>StopLossApproximation</c> と同じ作法。IADR-0399）。
/// 2 か所に置くと片方だけが変わり、新規建てと遡及で下限が食い違う。
/// </para>
/// </summary>
public static class StopWidthFloorDefaults
{
    /// <summary>ATR(14) が得られないときの下限＝参照価格 × 2%（出所 <see cref="StopWidthFloorSource.Fallback2Pct"/>）。</summary>
    public const decimal FallbackRatio = 0.02m;
}
