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
}
