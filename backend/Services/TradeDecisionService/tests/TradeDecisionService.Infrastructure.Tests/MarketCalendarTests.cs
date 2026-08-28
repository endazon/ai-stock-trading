using TradeDecisionService.Infrastructure.Adapters;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace TradeDecisionService.Infrastructure.Tests;

// FR-02, UC-01, IADR-0023, #337, IADR-0245: 市場カレンダー
// （週末・構成休場日・半日取引日・市場ローカル TZ での場中判定）を検証する。
//
// 市場時刻の判定はすべて市場ローカル時刻（米国＝米国東部時間）で行う（計画 04_workflows/01）。
// DST 切替日をまたぐテーブルは **UTC の同一時刻が切替の前後で別の判定になる**ことまで固定する
// （固定オフセット換算に退行すると、ここが必ず割れる）。
public class MarketCalendarTests
{
    private static MarketCalendar Calendar(
        IReadOnlyDictionary<Market, IReadOnlySet<DateOnly>>? holidays = null,
        IReadOnlyDictionary<Market, IReadOnlySet<DateOnly>>? halfDays = null) =>
        new(holidays ?? new Dictionary<Market, IReadOnlySet<DateOnly>>(), halfDays);

    private static IReadOnlyDictionary<Market, IReadOnlySet<DateOnly>> Dates(Market market, params DateOnly[] dates) =>
        new Dictionary<Market, IReadOnlySet<DateOnly>> { [market] = new HashSet<DateOnly>(dates) };

    // JST（+9）での特定日時。
    private static DateTimeOffset Jst(int y, int m, int d, int hour = 10, int minute = 0) =>
        new(y, m, d, hour, minute, 0, TimeSpan.FromHours(9));

    // UTC での特定日時（DST 切替テーブル用。ET への写像は実装の TimeZoneInfo に行わせる）。
    private static DateTimeOffset Utc(int y, int m, int d, int hour, int minute = 0) =>
        new(y, m, d, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void 平日の場中は開場()
    {
        // 2026-07-13 は月曜。前場 10:00 JST。
        Calendar().IsOpen(Market.Japan, Jst(2026, 7, 13)).Should().BeTrue();
    }

    [Fact]
    public void 週末は非開場()
    {
        // 2026-07-11 土 / 2026-07-12 日（いずれも場中に当たる時刻で判定する）。
        Calendar().IsOpen(Market.Japan, Jst(2026, 7, 11)).Should().BeFalse();
        Calendar().IsOpen(Market.Japan, Jst(2026, 7, 12)).Should().BeFalse();
    }

    [Fact]
    public void 構成された休場日は非開場()
    {
        var holidays = Dates(Market.Japan, new DateOnly(2026, 7, 13));

        // 月曜の場中だが休場日に設定 → 非開場。
        Calendar(holidays).IsOpen(Market.Japan, Jst(2026, 7, 13)).Should().BeFalse();
        // 別市場（米国）は影響を受けない（US Eastern 月曜 10:00 EDT = 14:00 UTC → 開場）。
        Calendar(holidays).IsOpen(Market.UnitedStates, Utc(2026, 7, 13, 14)).Should().BeTrue();
    }

    // --- 東証の時刻構造（前場 9:00–11:30／後場 12:30–15:30。計画の対比表） ---

    [Theory]
    [InlineData(8, 59, false)]  // 寄付き前
    [InlineData(9, 0, true)]    // 前場開始
    [InlineData(11, 29, true)]  // 前場末
    [InlineData(11, 30, false)] // 昼休み開始
    [InlineData(12, 0, false)]  // 昼休み
    [InlineData(12, 30, true)]  // 後場開始
    [InlineData(15, 29, true)]  // 後場末
    [InlineData(15, 30, false)] // 大引け後
    public void 東証は前場後場の時間帯で判定する(int hour, int minute, bool expected)
    {
        // 2026-07-13 は月曜。
        Calendar().IsOpen(Market.Japan, Jst(2026, 7, 13, hour, minute)).Should().Be(expected);
    }

    [Fact]
    public void 東証には半日取引日が無いため構成しても無視される()
    {
        // 半日集合に入れても後場 14:00 JST は開場のまま（計画: 東証の半日取引日は「なし」）。
        var halfDays = Dates(Market.Japan, new DateOnly(2026, 7, 13));
        Calendar(halfDays: halfDays).IsOpen(Market.Japan, Jst(2026, 7, 13, 14)).Should().BeTrue();
    }

    // --- 米国市場の時刻構造（9:30–16:00 ET・DST 切替。計画の対比表） ---
    //
    // 2026 年の DST: 3/8（第 2 日曜）に EST→EDT、11/1（第 1 日曜）に EDT→EST。
    // 切替の前後で **同じ UTC 時刻が別の判定になる**（差が 1 時間ずれるため固定オフセットでは換算できない）。

    [Theory]
    // EST（UTC-5）の金曜 2026-03-06: 9:30 ET = 14:30 UTC。
    [InlineData(3, 6, 14, 29, false)]  // 9:29 EST 寄付き前
    [InlineData(3, 6, 14, 30, true)]   // 9:30 EST 開場
    [InlineData(3, 6, 20, 59, true)]   // 15:59 EST 場中
    [InlineData(3, 6, 21, 0, false)]   // 16:00 EST Closing Cross（連続売買の場中ではない）
    // 同じ 13:30 UTC が、切替前は 8:30 EST（場外）・切替後は 9:30 EDT（開場）。
    [InlineData(3, 6, 13, 30, false)]
    [InlineData(3, 9, 13, 30, true)]   // EDT（UTC-4）の月曜 2026-03-09
    [InlineData(3, 9, 19, 59, true)]   // 15:59 EDT
    [InlineData(3, 9, 20, 0, false)]   // 16:00 EDT
    // 11 月の戻り: 金曜 2026-10-30 は EDT、月曜 2026-11-02 は EST。
    [InlineData(10, 30, 13, 30, true)] // 9:30 EDT
    [InlineData(11, 2, 13, 30, false)] // 8:30 EST 寄付き前
    [InlineData(11, 2, 14, 30, true)]  // 9:30 EST
    public void 米国市場はDST切替をまたいでも東部時間で判定する(int month, int day, int utcHour, int utcMinute, bool expected)
    {
        Calendar().IsOpen(Market.UnitedStates, Utc(2026, month, day, utcHour, utcMinute)).Should().Be(expected);
    }

    [Theory]
    // 2026-11-27（感謝祭翌日・金曜・EST）を半日取引日に構成: 13:00 ET 終了。
    [InlineData(17, 59, true)]  // 12:59 EST 場中
    [InlineData(18, 0, false)]  // 13:00 EST 半日の大引け
    [InlineData(20, 59, false)] // 15:59 EST（通常なら場中だが半日なので閉場）
    public void 半日取引日は13時ETで閉場する(int utcHour, int utcMinute, bool expected)
    {
        var halfDays = Dates(Market.UnitedStates, new DateOnly(2026, 11, 27));
        Calendar(halfDays: halfDays).IsOpen(Market.UnitedStates, Utc(2026, 11, 27, utcHour, utcMinute))
            .Should().Be(expected);
    }

    [Fact]
    public void 半日構成が無ければ同じ日の午後も通常どおり開場する()
    {
        // 対の肯定形: 半日を構成しなければ 15:59 EST（20:59 UTC）は場中。
        Calendar().IsOpen(Market.UnitedStates, Utc(2026, 11, 27, 20, 59)).Should().BeTrue();
    }
}
