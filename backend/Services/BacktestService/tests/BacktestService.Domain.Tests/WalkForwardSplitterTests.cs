using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Domain.Tests;

// FR-15, ADR-0008, 06_daytrading-review §3.2: ウォークフォワード検証。In-Sample→Out-of-Sample の窓分割を検証する。
public class WalkForwardSplitterTests
{
    private static readonly DateOnly Start = new(2024, 1, 1);
    private static readonly DateOnly End = new(2024, 1, 10);

    [Fact]
    public void ローリングはIS幅OOS幅固定でスライドする()
    {
        var windows = WalkForwardSplitter.Rolling(Start, End, inSampleDays: 4, outOfSampleDays: 2);

        windows.Should().HaveCount(3);
        windows[0].Should().Be(new WalkForwardWindow(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 4),
            new DateOnly(2024, 1, 5), new DateOnly(2024, 1, 6)));
        windows[1].InSampleStart.Should().Be(new DateOnly(2024, 1, 3)); // OOS 幅ぶんスライド
        windows[2].OutOfSampleEnd.Should().Be(new DateOnly(2024, 1, 10));
    }

    [Fact]
    public void アンカーはIS起点固定でISが拡大する()
    {
        var windows = WalkForwardSplitter.Anchored(Start, End, initialInSampleDays: 4, outOfSampleDays: 2);

        windows.Should().HaveCount(3);
        windows.Should().OnlyContain(w => w.InSampleStart == Start); // 起点固定
        windows[0].InSampleEnd.Should().Be(new DateOnly(2024, 1, 4));
        windows[1].InSampleEnd.Should().Be(new DateOnly(2024, 1, 6)); // 拡大
        windows[2].OutOfSampleEnd.Should().Be(new DateOnly(2024, 1, 10));
    }

    [Fact]
    public void OOSが期間内に収まらない窓は生成しない()
    {
        // IS 8 日 + OOS 5 日 = 13 日 > 10 日 → 窓なし。
        WalkForwardSplitter.Rolling(Start, End, inSampleDays: 8, outOfSampleDays: 5)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(4, 0)]
    [InlineData(-1, 2)]
    public void 非正の窓幅は例外(int isDays, int oosDays)
    {
        var act = () => WalkForwardSplitter.Rolling(Start, End, isDays, oosDays);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
