extern alias RiskManagementWorker;

using AiStockTrading.Shared.Contracts.Trading;
using RiskManagementWorker::RiskManagementService.Domain;

namespace TradeDecisionService.Features.TradeDecision.DecideTrade;

// FR-10, ADR-0003, ADR-0049 決定1〜3, #1120, IADR-0465 決定1: 損切り幅の下限を掛ける純関数。
// AI（取引判断 LLM）が幅を提案し、系が決定的なコードで下限を掛ける（AI は下限を上書きできない）。
// 適用する幅 ＝ max(AI の幅, 下限)。下限を割った幅は下限まで広げ、**見送らない**。上限は設けない。
// 値はすべて 1 株あたり・銘柄のローカル通貨（基準通貨への換算はサイジングの直前に呼び出し側が行う。IADR-0107）。
public static class StopWidthFloorPolicy
{
    /// <summary>
    /// ATR が得られないときの下限＝<paramref name="referencePrice"/>（アンカー後の現在値。Stage 0 は記録の参照価格）× 2%
    /// （<see cref="TradingDefaults.StopWidthFloorFallbackRatio"/>。§5「損切り幅の下限」）。端数は丸めない。
    /// </summary>
    public static StopWidthFloor Fallback(decimal referencePrice)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(referencePrice);
        return new StopWidthFloor(
            referencePrice * TradingDefaults.StopWidthFloorFallbackRatio, StopWidthFloorSource.Fallback2Pct);
    }

    /// <summary>
    /// FR-10, ADR-0049 決定2, #1122, IADR-0486 決定3: ATR(14) から下限を作る（下限 ＝ <see cref="TradingDefaults.StopWidthFloorAtrMultiple"/>（1.0）× ATR。
    /// 出所 <see cref="StopWidthFloorSource.Atr14"/>）。ATR が得られない（null・0 以下）ときは null（呼び出し側が退避の 2% を使う）。端数は丸めない。
    /// </summary>
    public static StopWidthFloor? FromAtr(decimal? atr) =>
        atr is decimal value && value > 0m
            ? new StopWidthFloor(value * TradingDefaults.StopWidthFloorAtrMultiple, StopWidthFloorSource.Atr14, value)
            : null;

    /// <summary>
    /// 供給口の答え（<paramref name="supplied"/>。null＝得られない）を検める。正の値だけを採り、それ以外は退避の 2% にする。
    /// </summary>
    public static StopWidthFloor Resolve(StopWidthFloor? supplied, decimal referencePrice) =>
        supplied is { PerShare: > 0m } floor && floor.Source != StopWidthFloorSource.Unspecified
            ? floor
            : Fallback(referencePrice);

    /// <summary>AI の幅に下限を掛ける。広げたかは AI の幅が下限を**割った**ときだけ true（ちょうど下限は false）。</summary>
    public static StopWidthFloorApplication Apply(decimal aiWidthPerShare, StopWidthFloor floor)
    {
        var widened = aiWidthPerShare < floor.PerShare;
        return new StopWidthFloorApplication(
            aiWidthPerShare,
            floor.PerShare,
            floor.Source,
            widened ? floor.PerShare : aiWidthPerShare,
            widened);
    }
}
