using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.RetrofitStopWidthFloor;

// 🔴 FR-10, ADR-0049 決定2・決定3, #1136（オーナー裁定 2026-10-01）, IADR-0472 決定2: 既存の S1 の損切りラインへ下限を遡及する純関数。
//   下限        ＝ 取得単価 × 2%（StopWidthFloorDefaults.FallbackRatio。出所 Fallback2Pct。発注執行は ATR を持たない）
//   下限のライン ＝ 取得単価 − 下限（買い建て）／ 取得単価 ＋ 下限（売り建て）
//   新しいライン ＝ 買い建て min(今のライン, 下限のライン)／売り建て max(今のライン, 下限のライン)
// 🔴 **広げる向きだけ**。等しい・狭める向きは変えない（null）。端数は丸めない（S1 のラインは数値で比べる。S0 / S3 は対象外）。
// min / max なので何度当てても同じ値に留まる（冪等）。
public static class StopWidthFloorRetrofitPolicy
{
    /// <summary>下限（1 株あたり・取得単価と同じローカル通貨）。</summary>
    public static decimal FloorPerShare(decimal entryPrice)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entryPrice);
        return entryPrice * StopWidthFloorDefaults.FallbackRatio;
    }

    /// <summary>取得単価から下限だけ離したライン（買い建ては下・売り建ては上）。</summary>
    public static decimal FloorLine(TradeSide entrySide, decimal entryPrice)
    {
        var floor = FloorPerShare(entryPrice);
        return entrySide == TradeSide.Buy ? entryPrice - floor : entryPrice + floor;
    }

    /// <summary>
    /// 広げた後のライン。下限のラインが今のラインより<b>外側</b>（買い建ては低い・売り建ては高い）のときだけ値を返し、
    /// それ以外（既に下限以上の幅・ちょうど下限）は null（変えない）。
    /// </summary>
    public static decimal? Widen(TradeSide entrySide, decimal currentLine, decimal entryPrice)
    {
        var floorLine = FloorLine(entrySide, entryPrice);
        return entrySide == TradeSide.Buy
            ? floorLine < currentLine ? floorLine : null
            : floorLine > currentLine ? floorLine : null;
    }
}
