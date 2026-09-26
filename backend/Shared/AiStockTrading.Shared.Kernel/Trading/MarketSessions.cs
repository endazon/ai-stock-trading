using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Kernel.Trading;

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

    /// <summary>
    /// FR-03, #909, IADR-0380 決定3: 同じ日のうち、<paramref name="localTime"/> より**後**に始まる最初のセッションの
    /// 開始時刻。無ければ <c>null</c>（＝その日はもう始まらない）。**次の開場時刻を出すためだけの補助**であり、
    /// 場中判定は <see cref="IsWithinSession"/> が持つ（境界の定義を 2 か所に置かない）。
    /// </summary>
    /// <param name="market">市場。</param>
    /// <param name="localTime">市場ローカルの時刻。</param>
    /// <param name="isHalfDay">半日取引日か。**開始時刻は前倒しされない**（前倒しされるのは終了時刻だけ）。</param>
    public static TimeOnly? NextSessionStart(Market market, TimeOnly localTime, bool isHalfDay) => market switch
    {
        // 半日取引日でも寄り付きは 9:30 のまま。既に 9:30 を過ぎていればその日はもう始まらない。
        Market.UnitedStates => localTime < UsOpen ? UsOpen : null,

        // 前場 → 後場の 2 段。昼休み中（11:30–12:30）は後場の寄り付きが「次の開場」である。
        Market.Japan =>
            localTime < JpMorningOpen ? JpMorningOpen
            : localTime < JpAfternoonOpen ? JpAfternoonOpen
            : null,

        _ => null,
    };

    /// <summary>
    /// FR-03, ADR-0043（計画）決定 3, #1030, IADR-0437: 通常の取引日の場中の長さ（分）。米国 390（9:30–16:00）・東証 330
    /// （前場 150 ＋ 後場 180）・未知の市場 0。<b>1 日の巡回回数を開場中の巡回だけで数える</b>ための値であり、時刻の定数は
    /// <see cref="IsWithinSession"/> と同じもの（境界の定義を 2 か所に置かない）から導く。半日取引日は数えない（通常日の上限）。
    /// </summary>
    public static int RegularSessionMinutes(Market market) => market switch
    {
        Market.UnitedStates => Minutes(UsOpen, UsClose),
        Market.Japan => Minutes(JpMorningOpen, JpMorningClose) + Minutes(JpAfternoonOpen, JpAfternoonClose),
        _ => 0,
    };

    private static int Minutes(TimeOnly from, TimeOnly to) => (int)(to - from).TotalMinutes;
}
