using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// FR-04, FR-02, ADR-0048 決定 2・3, ADR-0049 決定 2, #1118, IADR-0467 決定 3: 判断側から日足（前復権の OHLCV）を読む口。
// 判断へ渡す出来高（前営業日の出来高・20 日平均比）がこれを読み、損切り幅の下限の ATR(14)（#1122）も同じ口を再利用する。
//
// 🔴 **返すのは前営業日までの確定足だけである**（当日の未確定足は含めない）。OpenD は当日を含む期間に未確定の当日足を返す
// （#1117 の実測: 出来高が平常の約 1/3）。当日を前営業日として読むと、比が「出来高が細った」に見える。
// 🔴 **null＝取得できない（不明）**。空や 0 を「出来高が無い」の意味で返さない。取得できないことで判断を止めない（ADR-0048 決定 2）。
// 🔴 **IsEnabled=false の実装は外へ 1 回も要求しない**（既定。取得枠に触れない）。判断はそのときプロンプトを従来のまま保つ。
public interface IDailyBarsProvider
{
    /// <summary>判断の出来高が有効化されているか（既定 false）。false の実装は要求を出さず常に null を返す。</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// 判断時点の取引日（市場ローカル）より前の確定した日足を、日付の昇順で返す。取得できなければ null。
    /// キャンセル以外の失敗は null で返し、例外にしない（実装の約束。呼び出し側も保険として握る）。
    /// </summary>
    Task<ConfirmedDailyBars?> GetConfirmedBarsAsync(string symbol, Market market, CancellationToken cancellationToken = default);

    /// <summary>
    /// FR-15, ADR-0048 決定 2, #1139, IADR-0479 決定 1: <b>過去の判断時点（Stage 0 の AsOf）</b>を取引日として、それより前の確定した日足を返す。
    /// 期間の求め方と確定足の切り方は <see cref="GetConfirmedBarsAsync"/> と同じ（取引日以降の足は返さない＝先読みしない）。
    /// 本番のキャッシュは読まない・書かない。IsEnabled=false の実装は要求せず null。取得できなければ null（キャンセル以外は例外にしない）。
    /// </summary>
    Task<ConfirmedDailyBars?> GetConfirmedBarsAsOfAsync(
        string symbol, Market market, DateOnly tradingDay, CancellationToken cancellationToken = default);
}

// 日足 1 本（前復権の OHLCV・ローカル通貨）。True Range（ATR）は High・Low・前日の Close を使う。
public sealed record DailyBar(DateOnly Date, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

// 前営業日までの確定足。TradingDay は判断時点の取引日（市場ローカルの日付）、ExpectedPreviousTradingDay はその前営業日
// （週末と規則計算の休場日を除いた前日）。Bars は TradingDay より前の足だけを日付の昇順で持つ（最後が前営業日とは限らない）。
public sealed record ConfirmedDailyBars(DateOnly TradingDay, DateOnly ExpectedPreviousTradingDay, IReadOnlyList<DailyBar> Bars);

// FR-04, #1118, IADR-0467 決定 3: 取引日の計算（市場ローカルの日付・前営業日）。
// 🔴 **休場日は共有カーネルの規則計算（MarketHolidays）だけを見る。** 構成で足す臨時休場（TradeCycle:Holidays）は見ない ——
// 臨時休場の翌営業日は「前営業日の足が無い」と読んで出来高を「未提供」にする（古い足を前日と書くより安全側）。
public static class MarketTradingDays
{
    /// <summary>その瞬間の、市場ローカル時刻での日付（米国株は米国東部）。</summary>
    public static DateOnly TradingDateOf(Market market, DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, MarketHours.ZoneOf(market)).DateTime);

    /// <summary>その日より前の最後の取引日（週末と規則計算の休場日を除く）。</summary>
    public static DateOnly PreviousTradingDay(Market market, DateOnly date)
    {
        var d = date.AddDays(-1);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || MarketHolidays.IsHoliday(market, d))
            d = d.AddDays(-1);
        return d;
    }
}
