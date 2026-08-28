using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.TradeDecision.Domain;

// FR-02, UC-01, #337, IADR-0245: 市場の時刻構造（計画 04_workflows/01「市場の時刻構造への対応」の対比表を写像）。
//
//   | 市場 | 通常取引時間 | 半日取引日 |
//   | 米国 | 9:30–16:00 ET（昼休みなし） | あり（感謝祭翌日・クリスマスイブ等。13:00 ET 終了） |
//   | 東証 | 前場 9:00–11:30／後場 12:30–15:30 | なし |
//
// 🔴 **本型はタイムゾーン変換を持たない**（IADR-0137 決定1 と同じ規律）。現地時刻への写像は供給側
// （MarketCalendar）が TimeZoneInfo で行う——サマータイムの切替で日本時間との差が 1 時間ずれるため、
// **固定のオフセットで換算しない**（計画の明文）。ここは「現地時刻が場中か」という純関数だけを持つ。
//
// 境界の扱い: 開始は包含・終了は排他。米国の 16:00 は Closing Cross（当日決済のクローズ）であり、
// 16:00:00 ちょうどは連続売買の場中ではない。東証も同様に 11:30 / 15:30 ちょうどは場中に含めない
// （連続売買は実質 15:25 までだが、クロージング・オークションまでを場中として扱う）。
public static class MarketSessions
{
    private static readonly TimeOnly UsOpen = new(9, 30);
    private static readonly TimeOnly UsClose = new(16, 0);
    private static readonly TimeOnly UsHalfDayClose = new(13, 0);

    private static readonly TimeOnly JpMorningOpen = new(9, 0);
    private static readonly TimeOnly JpMorningClose = new(11, 30);
    private static readonly TimeOnly JpAfternoonOpen = new(12, 30);
    private static readonly TimeOnly JpAfternoonClose = new(15, 30);

    /// <summary>
    /// 現地時刻（市場ローカル）が取引時間内か。曜日・休場日の判定は呼び出し側（カレンダー）の責務。
    /// </summary>
    /// <param name="market">市場。</param>
    /// <param name="localTime">市場ローカルの時刻（TimeZoneInfo で変換済みであること）。</param>
    /// <param name="isHalfDay">半日取引日か。**東証には半日取引日が無いため無視される**（計画の対比表）。</param>
    public static bool IsWithinSession(Market market, TimeOnly localTime, bool isHalfDay) => market switch
    {
        // 昼休みがないため連続。半日取引日は終了時刻が前倒しになる（13:00 ET 終了）。
        Market.UnitedStates => localTime >= UsOpen && localTime < (isHalfDay ? UsHalfDayClose : UsClose),

        // 前場・後場の 2 セッション。11:30 スロットの判断は昼休み跨ぎのギャップリスクを考慮する（計画）
        // ため、昼休み（11:30–12:30）は場中に含めない。
        Market.Japan =>
            (localTime >= JpMorningOpen && localTime < JpMorningClose)
            || (localTime >= JpAfternoonOpen && localTime < JpAfternoonClose),

        // 未知の市場は安全側（場外＝サイクルを起動しない）。ADR-0003「不確実なら取引しない」と同じ向き。
        _ => false,
    };
}
