using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Kernel.Tests.Trading;

// FR-03, FR-10, UC-02, #909, IADR-0380 決定1・決定4: 「今その市場は場中か」の単一情報源。
//
// 時刻はすべて引数で注入する（壁時計・実時間の待ちを使わない）。UTC で与え、判定は市場ローカル時刻で行われる
// —— 夏時間の両端で**同じ現地時刻が同じ判定**になることが、固定オフセット換算をしていないことの証拠である。
public class MarketHoursTests
{
    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static IReadOnlySet<DateOnly> Dates(params DateOnly[] dates) => new HashSet<DateOnly>(dates);

    [Theory]
    // 2026-09-23 は水曜・EDT（UTC-4）。開始は包含・終了は排他（16:00 ちょうどは場中でない）。
    [InlineData(2026, 9, 23, 13, 29, false)] // 9:29 ET 寄付き前
    [InlineData(2026, 9, 23, 13, 30, true)]  // 9:30 ET 寄付き
    [InlineData(2026, 9, 23, 19, 59, true)]  // 15:59 ET 大引け直前
    [InlineData(2026, 9, 23, 20, 0, false)]  // 16:00 ET 大引け（#909 の事故はこの先で起きていた）
    [InlineData(2026, 9, 23, 23, 0, false)]  // 19:00 ET 時間外
    [InlineData(2026, 9, 26, 13, 30, false)] // 土曜
    [InlineData(2026, 9, 27, 13, 30, false)] // 日曜
    public void T_10_690_米国市場は通常取引時間の内側だけ場中になる(
        int year, int month, int day, int utcHour, int utcMinute, bool expected)
    {
        // T-10-690, FR-03, FR-10, #909, IADR-0380 決定1
        MarketHours.IsOpen(Market.UnitedStates, Utc(year, month, day, utcHour, utcMinute)).Should().Be(expected);
    }

    [Theory]
    // 夏時間の両端（2026 は 3/8 開始・11/1 終了）。**同じ現地時刻 9:30 ET が、EST でも EDT でも寄付きである。**
    [InlineData(2026, 3, 6, 14, 30, true)]   // 9:30 EST（UTC-5）
    [InlineData(2026, 3, 6, 13, 30, false)]  // 8:30 EST
    [InlineData(2026, 3, 9, 13, 30, true)]   // 9:30 EDT（UTC-4・切替の翌営業日）
    [InlineData(2026, 3, 9, 12, 30, false)]  // 8:30 EDT
    [InlineData(2026, 10, 30, 13, 30, true)] // 9:30 EDT（切替の直前営業日）
    [InlineData(2026, 11, 2, 14, 30, true)]  // 9:30 EST（切替の翌営業日）
    [InlineData(2026, 11, 2, 13, 30, false)] // 8:30 EST
    public void T_10_691_夏時間の両端でも同じ現地時刻が同じ判定になる(
        int year, int month, int day, int utcHour, int utcMinute, bool expected)
    {
        // T-10-691, FR-03, #909, IADR-0380 決定1（固定オフセットで換算していないことの証拠）
        MarketHours.IsOpen(Market.UnitedStates, Utc(year, month, day, utcHour, utcMinute)).Should().Be(expected);
    }

    [Theory]
    // 2026 年の休場日（規則計算。構成は与えない）。時刻はいずれも 15:00 UTC＝通常なら場中。
    [InlineData(2026, 1, 1)]   // 元日（木）
    [InlineData(2026, 1, 19)]  // キング牧師記念日（1 月第 3 月曜）
    [InlineData(2026, 2, 16)]  // ワシントン誕生日（2 月第 3 月曜）
    [InlineData(2026, 4, 3)]   // グッドフライデー（復活祭 4/5 の 2 日前）
    [InlineData(2026, 5, 25)]  // 戦没者追悼日（5 月最終月曜）
    [InlineData(2026, 6, 19)]  // ジューンティーンス（金）
    [InlineData(2026, 7, 3)]   // 独立記念日の振替（7/4 が土曜 → 前日の金曜）
    [InlineData(2026, 9, 7)]   // レイバーデー（9 月第 1 月曜）
    [InlineData(2026, 11, 26)] // 感謝祭（11 月第 4 木曜）
    [InlineData(2026, 12, 25)] // クリスマス（金）
    [InlineData(2023, 1, 2)]   // 元日が日曜 → 翌月曜へ振替
    public void T_10_692_規則計算の休場日は構成が空でも閉場する(int year, int month, int day)
    {
        // T-10-692, FR-03, #909, IADR-0380 決定4（**日付表を持たない＝期限が無い**）
        MarketHours.IsOpen(Market.UnitedStates, Utc(year, month, day, 15, 0)).Should().BeFalse();
    }

    [Theory]
    // 対（肯定形）: 休場日の隣接営業日は開場する。**全部閉場と答える実装でも上の表は緑になる。**
    [InlineData(2026, 1, 2)]   // 元日の翌営業日（金）
    [InlineData(2026, 4, 6)]   // グッドフライデーの翌営業日（月）
    [InlineData(2026, 7, 6)]   // 独立記念日の振替の翌営業日（月）
    [InlineData(2026, 11, 30)] // 感謝祭週の翌月曜
    [InlineData(2022, 1, 3)]   // 元日が土曜の年（2022-01-01）は振替せず、最初の営業日は 1/3
    [InlineData(2021, 6, 18)]  // ジューンティーンスは 2022 年から（2021-06-18 は通常営業）
    public void T_10_692_休場日の隣接営業日は開場する(int year, int month, int day)
    {
        // T-10-692（対の肯定形）, FR-03, #909, IADR-0380 決定4
        MarketHours.IsOpen(Market.UnitedStates, Utc(year, month, day, 15, 0)).Should().BeTrue();
    }

    [Theory]
    // 半日取引日（13:00 ET 終了）。2026-11-27 は感謝祭翌日（EST）、2026-12-24 は木曜のクリスマスイブ（EST）。
    [InlineData(2026, 11, 27, 17, 59, true)]  // 12:59 EST
    [InlineData(2026, 11, 27, 18, 0, false)]  // 13:00 EST 半日の大引け
    [InlineData(2026, 11, 27, 20, 59, false)] // 15:59 EST（通常日なら場中）
    [InlineData(2026, 12, 24, 18, 0, false)]  // 13:00 EST
    [InlineData(2026, 12, 24, 17, 59, true)]  // 12:59 EST
    [InlineData(2025, 7, 3, 18, 0, false)]    // 13:00 EDT（2025-07-04 は金曜なので 7/3 は半日）
    public void T_10_692_半日取引日は13時ETで閉場する(
        int year, int month, int day, int utcHour, int utcMinute, bool expected)
    {
        // T-10-692, FR-03, #909, IADR-0380 決定4
        MarketHours.IsOpen(Market.UnitedStates, Utc(year, month, day, utcHour, utcMinute)).Should().Be(expected);
    }

    [Fact]
    public void T_10_693_次の開場は週末と休場日を跨いで返る()
    {
        // T-10-693, FR-03, #909, IADR-0380 決定3
        // 2026-11-25（水）16:00 EST の直後。翌 11/26 は感謝祭で休場、11/27 は半日だが**寄り付きは 9:30 のまま**。
        MarketHours.NextOpen(Market.UnitedStates, Utc(2026, 11, 25, 21, 0))
            .Should().Be(new DateTimeOffset(2026, 11, 27, 9, 30, 0, TimeSpan.FromHours(-5)));

        // 半日の大引け（11/27 13:00 EST）の直後は、週末を跨いで 11/30（月）の寄り付き。
        MarketHours.NextOpen(Market.UnitedStates, Utc(2026, 11, 27, 18, 0))
            .Should().Be(new DateTimeOffset(2026, 11, 30, 9, 30, 0, TimeSpan.FromHours(-5)));

        // 場中に尋ねたら「次の」寄り付き＝翌営業日（今まさに開いている場は返さない）。
        MarketHours.NextOpen(Market.UnitedStates, Utc(2026, 9, 23, 15, 0))
            .Should().Be(new DateTimeOffset(2026, 9, 24, 9, 30, 0, TimeSpan.FromHours(-4)));
    }

    [Fact]
    public void T_10_693_東証の昼休み中の次の開場は同じ日の後場である()
    {
        // T-10-693, FR-03, #909, IADR-0380 決定3（市場ごとに時刻構造が違う）
        // 2026-09-24（木）11:45 JST＝02:45 UTC。次の開場は同日 12:30 JST。
        MarketHours.NextOpen(Market.Japan, Utc(2026, 9, 24, 2, 45))
            .Should().Be(new DateTimeOffset(2026, 9, 24, 12, 30, 0, TimeSpan.FromHours(9)));
    }

    [Fact]
    public void T_10_694_構成の臨時休場日は規則計算へ足され_規則は外せない()
    {
        // T-10-694, FR-03, #909, IADR-0380 決定4
        var extra = Dates(new DateOnly(2026, 9, 23));

        // 足す: 規則では通常営業の水曜でも、構成に入れれば閉場。
        MarketHours.IsOpen(Market.UnitedStates, Utc(2026, 9, 23, 15, 0), extra).Should().BeFalse();
        // 対の肯定形: 同じ構成でも、指定していない翌日は開場したまま。
        MarketHours.IsOpen(Market.UnitedStates, Utc(2026, 9, 24, 15, 0), extra).Should().BeTrue();

        // 外せない: 構成に何を渡しても規則計算の休場日（感謝祭）は閉場のままである。
        MarketHours.IsOpen(Market.UnitedStates, Utc(2026, 11, 26, 15, 0), Dates(), Dates()).Should().BeFalse();
    }

    [Fact]
    public void 東証は休場日の規則計算を持たない()
    {
        // 射程の明示（#21）: 日本の祝日は規則が国民の祝日法に依存するため本表の射程外で、週末と構成だけを見る。
        // 2026-01-01（元日・木）は東証も休場だが、**規則計算は米国市場にしか無い**ため開場と答える。
        MarketHours.IsOpen(Market.Japan, Utc(2026, 1, 1, 1, 0)).Should().BeTrue();
        MarketHours.IsOpen(Market.Japan, Utc(2026, 1, 1, 1, 0), Dates(new DateOnly(2026, 1, 1))).Should().BeFalse();
    }
}
