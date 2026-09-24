using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Kernel.Trading;

// FR-03, FR-02, UC-02, #909, #21, IADR-0380 決定4: 市場の休場日・半日取引日を**規則で計算する**（純関数）。
//
// 🔴 **日付表を持たない。** 表は必ず期限を持ち、期限が切れたことは「平常どおり動いている」ようにしか見えない
// （更新を促す仕組みが要る＝refresh 計画が要る）。米国市場の休場日は曜日規則と振替規則で完全に決まるため、
// **計算すれば期限が無い**。臨時休場（国葬・服喪・災害）だけは規則で書けないので、**構成から「足す」**
// （`Monitor:Holidays:<Market>` / `TradeCycle:Holidays:<Market>`）。構成で規則を**外すことはできない**
// —— 外せる経路を作ると、設定ミスが「閉場中に終値で損切りを回す」側（#909 の事故）へ倒れる。
//
// 対象は米国市場（NYSE / Nasdaq）の 10 日: 元日・キング牧師記念日・ワシントン誕生日・グッドフライデー・
// 戦没者追悼日・ジューンティーンス・独立記念日・レイバーデー・感謝祭・クリスマス。
// **コロンブスデーと復員軍人の日は連邦休日だが株式市場は開く**（債券市場とは休日が違う）。
//
// 東証の休場日は規則が国民の祝日法（春分・秋分は天文計算、振替休日、国民の休日）に依存し、
// **本表の射程外**である（週末＋構成注入のまま。#21 で扱う）。
public static class MarketHolidays
{
    /// <summary>その市場のその日が休場日か（週末は含まない。週末は呼び出し側が見る）。</summary>
    public static bool IsHoliday(Market market, DateOnly date) =>
        market == Market.UnitedStates && IsUnitedStatesHoliday(date);

    /// <summary>
    /// その市場のその日が半日取引日か（米国のみ実在。13:00 ET 終了）。
    /// 独立記念日前日（7/3 が平日で 7/4 が振替でないとき）・感謝祭翌日・クリスマスイブ（12/24 が月〜木のとき）。
    /// </summary>
    public static bool IsHalfDay(Market market, DateOnly date) =>
        market == Market.UnitedStates && IsUnitedStatesHalfDay(date);

    private static bool IsUnitedStatesHoliday(DateOnly date)
    {
        // 元日。土曜に当たる年は**振替しない**（前年 12/31 の金曜は通常どおり開く）。日曜は翌月曜へ。
        if (Observed(new DateOnly(date.Year, 1, 1), backwardOnSaturday: false) == date)
            return true;

        // キング牧師記念日（1 月第 3 月曜）・ワシントン誕生日（2 月第 3 月曜）。
        if (date == NthWeekday(date.Year, 1, DayOfWeek.Monday, 3) || date == NthWeekday(date.Year, 2, DayOfWeek.Monday, 3))
            return true;

        // グッドフライデー（復活祭前の金曜）。連邦休日ではないが株式市場は休場する。
        if (date == GoodFriday(date.Year))
            return true;

        // 戦没者追悼日（5 月最終月曜）・レイバーデー（9 月第 1 月曜）・感謝祭（11 月第 4 木曜）。
        if (date == LastWeekday(date.Year, 5, DayOfWeek.Monday)
            || date == NthWeekday(date.Year, 9, DayOfWeek.Monday, 1)
            || date == NthWeekday(date.Year, 11, DayOfWeek.Thursday, 4))
        {
            return true;
        }

        // 日付が決まっている 3 日（土曜は前倒し・日曜は後ろ倒し）。ジューンティーンスは 2022 年から。
        if (date.Year >= 2022 && Observed(new DateOnly(date.Year, 6, 19)) == date)
            return true;

        return Observed(new DateOnly(date.Year, 7, 4)) == date
            || Observed(new DateOnly(date.Year, 12, 25)) == date;
    }

    private static bool IsUnitedStatesHalfDay(DateOnly date)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || IsUnitedStatesHoliday(date))
            return false;

        // 感謝祭翌日（必ず金曜）。
        if (date == NthWeekday(date.Year, 11, DayOfWeek.Thursday, 4).AddDays(1))
            return true;

        // 独立記念日前日（7/3）。7/4 が土曜なら 7/3 は振替休日そのもの、日曜なら 7/3 は土曜であり、
        // どちらも上の 2 つの門で落ちる（＝7/4 が月〜金の年だけがここへ来る）。
        if (date == new DateOnly(date.Year, 7, 3))
            return true;

        // クリスマスイブ（12/24）。12/24 が金曜の年は 12/25 が土曜＝12/24 が振替休日そのもので、上の門で落ちる。
        return date == new DateOnly(date.Year, 12, 24);
    }

    /// <summary>土曜に当たる休日は前日の金曜へ、日曜に当たる休日は翌日の月曜へ振り替える。</summary>
    private static DateOnly Observed(DateOnly date, bool backwardOnSaturday = true) => date.DayOfWeek switch
    {
        DayOfWeek.Saturday => backwardOnSaturday ? date.AddDays(-1) : date,
        DayOfWeek.Sunday => date.AddDays(1),
        _ => date,
    };

    /// <summary>その月の第 n 曜日。</summary>
    private static DateOnly NthWeekday(int year, int month, DayOfWeek weekday, int n)
    {
        var first = new DateOnly(year, month, 1);
        var shift = ((int)weekday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(shift + ((n - 1) * 7));
    }

    /// <summary>その月の最終曜日。</summary>
    private static DateOnly LastWeekday(int year, int month, DayOfWeek weekday)
    {
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        return last.AddDays(-(((int)last.DayOfWeek - (int)weekday + 7) % 7));
    }

    /// <summary>グレゴリオ暦の復活祭（anonymous Gregorian algorithm）の 2 日前＝グッドフライデー。</summary>
    private static DateOnly GoodFriday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = ((19 * a) + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        var m = (a + (11 * h) + (22 * l)) / 451;
        var month = (h + l - (7 * m) + 114) / 31;
        var day = ((h + l - (7 * m) + 114) % 31) + 1;
        return new DateOnly(year, month, day).AddDays(-2);
    }
}
