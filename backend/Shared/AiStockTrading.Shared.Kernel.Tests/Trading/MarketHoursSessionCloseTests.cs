using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Kernel.Tests.Trading;

// T-10-2919, FR-10, FR-19, #1396, IADR-0561 決定 1: その日の最後の閉場（MarketHours.SessionCloseOn）と「閉場の N 分前以降か」（PreCloseWindow）。
// 閉場前の新規建ての締め切り（リスク管理）・閉場前の手仕舞い（取引判断）・判断のプロンプトの残り時間が同じ答えを見る。休場日・半日取引日・夏時間を固定する。
public class MarketHoursSessionCloseTests
{
    [Fact]
    public void T_10_2919_米国の通常の取引日は16時ET()
    {
        // 2026-10-19（月）夏時間: 16:00 EDT＝20:00 UTC。
        var close = MarketHours.SessionCloseOn(Market.UnitedStates, new DateTimeOffset(2026, 10, 19, 14, 0, 0, TimeSpan.Zero));

        close.Should().Be(new DateTimeOffset(2026, 10, 19, 20, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void T_10_2919_冬時間でも現地の16時である()
    {
        // 2026-11-02（月）冬時間: 16:00 EST＝21:00 UTC（固定のオフセットで換算しない）。
        MarketHours.SessionCloseOn(Market.UnitedStates, new DateTimeOffset(2026, 11, 2, 15, 0, 0, TimeSpan.Zero))
            .Should().Be(new DateTimeOffset(2026, 11, 2, 21, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void T_10_2919_半日取引日は13時ETへ前倒しされる()
    {
        // 2026-11-27（感謝祭の翌日）: 13:00 EST＝18:00 UTC。
        MarketHours.SessionCloseOn(Market.UnitedStates, new DateTimeOffset(2026, 11, 27, 15, 0, 0, TimeSpan.Zero))
            .Should().Be(new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void T_10_2919_構成で足した臨時の半日取引日も前倒しされる()
    {
        var extraHalfDay = new HashSet<DateOnly> { new(2026, 10, 19) };

        MarketHours.SessionCloseOn(
                Market.UnitedStates, new DateTimeOffset(2026, 10, 19, 14, 0, 0, TimeSpan.Zero), extraHalfDays: extraHalfDay)
            .Should().Be(new DateTimeOffset(2026, 10, 19, 17, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(2026, 11, 26)] // 感謝祭（休場日・規則計算）
    [InlineData(2026, 10, 17)] // 土曜
    [InlineData(2026, 10, 18)] // 日曜
    public void T_10_2919_休場日と週末は閉場が無い(int year, int month, int day)
    {
        MarketHours.SessionCloseOn(Market.UnitedStates, new DateTimeOffset(year, month, day, 18, 0, 0, TimeSpan.Zero))
            .Should().BeNull();
    }

    [Fact]
    public void T_10_2919_構成で足した臨時休場日は閉場が無い()
    {
        var extraHoliday = new HashSet<DateOnly> { new(2026, 10, 19) };

        MarketHours.SessionCloseOn(
                Market.UnitedStates, new DateTimeOffset(2026, 10, 19, 14, 0, 0, TimeSpan.Zero), extraHolidays: extraHoliday)
            .Should().BeNull();
    }

    [Fact]
    public void T_10_2919_東証は大引け15時30分JSTで昼休みは閉場ではない()
    {
        // 2026-10-20（火）10:00 JST（前場）でも閉場は 15:30 JST＝06:30 UTC。
        MarketHours.SessionCloseOn(Market.Japan, new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero))
            .Should().Be(new DateTimeOffset(2026, 10, 20, 6, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void T_10_2919_市場ローカルの日付で引く()
    {
        // 2026-10-20 02:00 UTC は米国東部では 10-19 22:00（10-19 の閉場の後）。閉場は 10-19 の 16:00 ET。
        MarketHours.SessionCloseOn(Market.UnitedStates, new DateTimeOffset(2026, 10, 20, 2, 0, 0, TimeSpan.Zero))
            .Should().Be(new DateTimeOffset(2026, 10, 19, 20, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(-61, false)]
    [InlineData(-60, true)]
    [InlineData(0, true)]
    [InlineData(30, true)]
    public void T_10_2919_閉場のN分前以降(int minutesFromClose, bool expected)
    {
        var close = new DateTimeOffset(2026, 10, 19, 20, 0, 0, TimeSpan.Zero);

        PreCloseWindow.IsAtOrAfter(close.AddMinutes(minutesFromClose), close, 60).Should().Be(expected);
    }

    [Fact]
    public void T_10_2919_閉場が無ければ入らず残り時間も無い()
    {
        var now = new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);

        PreCloseWindow.IsAtOrAfter(now, null, 60).Should().BeFalse();
        PreCloseWindow.Remaining(now, null).Should().BeNull();
    }

    [Fact]
    public void T_10_2919_残り時間は閉場の後は0()
    {
        var close = new DateTimeOffset(2026, 10, 19, 20, 0, 0, TimeSpan.Zero);

        PreCloseWindow.Remaining(close.AddMinutes(-58), close).Should().Be(TimeSpan.FromMinutes(58));
        PreCloseWindow.Remaining(close.AddMinutes(5), close).Should().Be(TimeSpan.Zero);
    }
}
