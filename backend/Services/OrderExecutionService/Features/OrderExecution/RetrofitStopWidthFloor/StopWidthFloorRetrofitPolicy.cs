using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.RetrofitStopWidthFloor;

// 🔴 FR-10, ADR-0049 決定2・決定3, #1136（オーナー裁定 2026-10-01）, IADR-0472 決定2: 既存の S1 の損切りラインへ下限を遡及する純関数。
//   下限        ＝ 取得単価 × 2%（StopWidthFloorDefaults.FallbackRatio。出所 Fallback2Pct。発注執行は ATR を持たない）
//   下限のライン ＝ 取得単価 − 下限（買い建て）／ 取得単価 ＋ 下限（売り建て）
//   新しいライン ＝ 買い建て min(今のライン, 下限のライン)／売り建て max(今のライン, 下限のライン)
// 🔴 **広げる向きだけ**。等しい・狭める向きは変えない（null）。端数は丸めない（S1 のラインは数値で比べる。S0 / S3 は対象外）。
// min / max なので何度当てても同じ値に留まる（冪等）。
// 🔴 #1136 独立監査 F2（IADR-0472 2026-10-01 追記）: 対象は**下限を割って建てた行**だけ（WasSizedBelowFloor）。
// 🔴 #1122, IADR-0486 決定7（IADR-0472 2026-10-03 追記）: **サイジングの時点で下限を掛けて建てた印のある行は対象にしない**（WasFloorAppliedAtSizing）。
// ATR の下限は参照価格の 2% より狭いことがあり、「参照価格から 2% 未満」の行を下限の導入前の行と取り違えて広げると、
// サイジングの想定（1 取引リスク 1%）より広い損切りになる（ADR-0049 決定1「系が掛ける下限は 1.0 × ATR」に反する）。
public static class StopWidthFloorRetrofitPolicy
{
    /// <summary>
    /// 🔴 #1122, IADR-0486 決定7: エントリーの発注記録の印（<c>ExecutionRecord.StopFloorSource</c>）が「下限を掛けてラインを引いた」
    /// （<see cref="StopWidthFloorSource.Fallback2Pct"/> / <see cref="StopWidthFloorSource.Atr14"/>）を示すか。示せば遡及しない。
    /// null・<see cref="StopWidthFloorSource.Unspecified"/>（#1122 より前の記録・分からない）は false（従来の <see cref="WasSizedBelowFloor"/> で判定する）。
    /// </summary>
    public static bool WasFloorAppliedAtSizing(StopWidthFloorSource? stopFloorSource) =>
        stopFloorSource is StopWidthFloorSource.Fallback2Pct or StopWidthFloorSource.Atr14;

    /// <summary>
    /// 🔴 #1136 独立監査 F2: この行のラインが、<b>ラインを引いた価格</b>（<paramref name="plannedPrice"/>＝エントリーの発注記録の
    /// PlannedPrice。取引判断の参照価格）から見て下限（2%）を割っているか。割っていれば「下限の導入前の幅で建てた行」として遡及の対象にする。
    /// <para>
    /// 下限の導入後の新規建ては、参照価格から max(AI の幅, 参照価格 × 2%) を引いてラインを作る（IADR-0465）。参照価格から見れば
    /// 必ず 2% 以上離れているので false になり、約定が参照価格より有利だった（取得単価から見ると 2% を割る）行でも二重に広げない。
    /// 導入前の行のうち AI の幅が参照価格の 2% 以上だった行も同じく対象外になる（下限そのものの基準は満たして建てた）。
    /// </para>
    /// <para>
    /// ラインを引いた価格が分からない（0 以下）ときは true（裁定の側＝遡及する。広げる向きだけなので損切りを早めない）。
    /// </para>
    /// </summary>
    public static bool WasSizedBelowFloor(TradeSide entrySide, decimal currentLine, decimal plannedPrice) =>
        plannedPrice <= 0m || Widen(entrySide, currentLine, plannedPrice) is not null;

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
