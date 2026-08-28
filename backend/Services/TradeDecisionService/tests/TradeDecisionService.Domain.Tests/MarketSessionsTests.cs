using AiStockTrading.TradeDecision.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.TradeDecision.Domain.Tests;

// FR-02, UC-01, #337, IADR-0245: 市場の時刻構造（純関数）の境界テーブル。
// タイムゾーン変換は持たない（現地時刻を入力とする）ため、ここでは時間帯の境界だけを固定する。
// DST・休場日・半日集合の合成は MarketCalendarTests が見る。
public class MarketSessionsTests
{
    [Theory]
    // 通常日: [9:30, 16:00)。
    [InlineData(9, 29, false, false)]
    [InlineData(9, 30, false, true)]
    [InlineData(12, 0, false, true)]   // 昼休みなし（東証との違い）
    [InlineData(15, 59, false, true)]
    [InlineData(16, 0, false, false)]  // Closing Cross は連続売買の場中ではない
    // 半日取引日: [9:30, 13:00)。
    [InlineData(9, 30, true, true)]
    [InlineData(12, 59, true, true)]
    [InlineData(13, 0, true, false)]
    [InlineData(15, 59, true, false)]
    public void 米国市場の時間帯境界(int hour, int minute, bool halfDay, bool expected)
    {
        MarketSessions.IsWithinSession(Market.UnitedStates, new TimeOnly(hour, minute), halfDay)
            .Should().Be(expected);
    }

    [Theory]
    // 前場 [9:00, 11:30) ∪ 後場 [12:30, 15:30)。
    [InlineData(8, 59, false)]
    [InlineData(9, 0, true)]
    [InlineData(11, 29, true)]
    [InlineData(11, 30, false)]
    [InlineData(12, 29, false)]
    [InlineData(12, 30, true)]
    [InlineData(15, 29, true)]
    [InlineData(15, 30, false)]
    public void 東証の前場後場境界(int hour, int minute, bool expected)
    {
        MarketSessions.IsWithinSession(Market.Japan, new TimeOnly(hour, minute), isHalfDay: false)
            .Should().Be(expected);
    }

    [Fact]
    public void 東証は半日フラグを無視する()
    {
        // 計画の対比表: 東証に半日取引日は「なし」。フラグの真偽で判定が変わらない。
        foreach (var t in new[] { new TimeOnly(10, 0), new TimeOnly(12, 0), new TimeOnly(14, 0) })
        {
            MarketSessions.IsWithinSession(Market.Japan, t, isHalfDay: true)
                .Should().Be(MarketSessions.IsWithinSession(Market.Japan, t, isHalfDay: false));
        }
    }

    [Fact]
    public void 未知の市場は安全側で場外()
    {
        // ADR-0003「不確実なら取引しない」——未定義の市場値は常に場外（サイクルを起動しない）。
        MarketSessions.IsWithinSession((Market)999, new TimeOnly(10, 0), isHalfDay: false).Should().BeFalse();
    }
}
