namespace RiskManagementService.Features.RiskManagement;

// FR-06, FR-16, #1186, IADR-0506 決定 2: 市場の現地取引日の条件を、台帳を SQL で読むときの約定時刻（UTC）の範囲へ**外包**で写す純関数。
//
// 🔴 **ここで返す範囲は「取りこぼさない上位集合」であり、正確な絞り込みではない。** 取引日の正確な判定
// （TradingDay.Of(instant, market)＝市場のタイムゾーン・夏時間）は従来どおり呼び出し側の純関数
// （OpeningInventoryQuery / PeriodFillQuery）が行う。SQL にタイムゾーンを持ち込まない。
//
// 根拠: どのタイムゾーンでも UTC との差は ±14 時間未満なので、ある瞬間の現地の暦日は UTC の暦日の前日・当日・翌日の
// いずれかである。したがって
//   - 取引日 <  before ⇒ UTC 暦日 <= before        ⇒ ExecutedAt <  (before + 1 日) 00:00Z
//   - 取引日 >= from   ⇒ UTC 暦日 >= from − 1 日   ⇒ ExecutedAt >= (from − 1 日) 00:00Z
//   - 取引日 <= to     ⇒ UTC 暦日 <= to + 1 日     ⇒ ExecutedAt <  (to + 2 日) 00:00Z
// 夏時間（ET の −4/−5 時間）はこの ±1 日の内側の揺れにすぎない。
public static class LedgerScanBounds
{
    /// <summary>取引日が <paramref name="beforeTradingDay"/> より前（排他）の行を取りこぼさない約定時刻の上限（排他）。溢れるなら null＝上限なし。</summary>
    public static DateTimeOffset? ExecutedBeforeForTradingDayBefore(DateOnly beforeTradingDay) =>
        UtcMidnight(beforeTradingDay, days: 1);

    /// <summary>取引日が <paramref name="fromTradingDay"/> 以降の行を取りこぼさない約定時刻の下限（含む）。溢れるなら null＝下限なし。</summary>
    public static DateTimeOffset? ExecutedAtOrAfterForTradingDayFrom(DateOnly fromTradingDay) =>
        UtcMidnight(fromTradingDay, days: -1);

    /// <summary>取引日が <paramref name="toTradingDay"/> 以前（含む）の行を取りこぼさない約定時刻の上限（排他）。溢れるなら null＝上限なし。</summary>
    public static DateTimeOffset? ExecutedBeforeForTradingDayTo(DateOnly toTradingDay) =>
        UtcMidnight(toTradingDay, days: 2);

    // DateOnly の端（0001-01-01・9999-12-31）で溢れる側は境界を外す（無制限＝従来の全行読みと同じ側へ倒す。値を発明しない）。
    private static DateTimeOffset? UtcMidnight(DateOnly day, int days) =>
        day.DayNumber + days < DateOnly.MinValue.DayNumber || day.DayNumber + days > DateOnly.MaxValue.DayNumber
            ? null
            : new DateTimeOffset(day.AddDays(days).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
