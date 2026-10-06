using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Kernel.Tests.Trading;

// FR-03, ADR-0043（計画）決定 3, #1030, IADR-0437: 1 日の巡回回数を開場中の巡回だけで数えるための場中の長さ。
public class MarketSessionMinutesTests
{
    // T-10-1430: 米国 390 分・東証 330 分（前場 150 ＋ 後場 180）・未知の市場 0。場中判定（IsWithinSession）を 1 分刻みで数えた長さと一致する
    // （境界の定義を 2 か所に置かない＝時刻の定数を変えれば両方が一緒に動く）。
    [Theory]
    [InlineData(Market.UnitedStates, 390)]
    [InlineData(Market.Japan, 330)]
    public void 場中の長さは場中判定と一致する(Market market, int expected)
    {
        MarketSessions.RegularSessionMinutes(market).Should().Be(expected);

        var counted = Enumerable.Range(0, 24 * 60)
            .Count(m => MarketSessions.IsWithinSession(market, TimeOnly.MinValue.AddMinutes(m), isHalfDay: false));
        counted.Should().Be(expected);
    }

    [Fact]
    public void 未知の市場は0()
    {
        MarketSessions.RegularSessionMinutes((Market)99).Should().Be(0);
    }

    // T-06-026, FR-06, #1172, IADR-0492 決定 2: 通常日の大引けは場中判定の終端と一致する（米国 16:00・東証 15:30・未知の市場 null）。
    // 報告書の「生成境界までに閉場したセッション」の判定に使う。直前の 1 分は場中・大引けちょうどは場外。
    [Theory]
    [InlineData(Market.UnitedStates, 16, 0)]
    [InlineData(Market.Japan, 15, 30)]
    public void 通常日の大引けは場中判定の終端と一致する(Market market, int hour, int minute)
    {
        var close = new TimeOnly(hour, minute);

        MarketSessions.RegularClose(market).Should().Be(close);
        MarketSessions.IsWithinSession(market, close.AddMinutes(-1), isHalfDay: false).Should().BeTrue();
        MarketSessions.IsWithinSession(market, close, isHalfDay: false).Should().BeFalse();
        MarketSessions.RegularClose((Market)99).Should().BeNull();
    }
}
