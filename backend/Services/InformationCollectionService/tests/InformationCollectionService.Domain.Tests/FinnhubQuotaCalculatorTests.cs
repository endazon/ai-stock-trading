using InformationCollectionService.Domain;
using AwesomeAssertions;
using Xunit;

namespace InformationCollectionService.Domain.Tests;

// FR-01, ADR-0020 §結果（フォローアップ）: 日次上限からの監視銘柄数の逆算。
//
// 🔴 **日次上限は未実測である。** 未実測（null）のときに数値を返さないことが本計算器の要点であり、
// 「推測値を実測として運用へ渡さない」ための構造である。
public class FinnhubQuotaCalculatorTests
{
    // 🔴 **未実測なら答えを出さない。** 出すと、その数値が「実測した上限」として運用に伝わる。
    [Fact]
    public void 日次上限が未実測なら銘柄数上限を返さない()
    {
        FinnhubQuotaCalculator.MaxWatchlistSymbols(null, cyclesPerDay: 13, requestsPerSymbolPerCycle: 2)
            .Should().BeNull();
    }

    [Theory]
    // 上限 300 回・1 日 13 巡回・1 銘柄 2 要求 → 300 / 26 = 11（端数切り捨て）
    [InlineData(300, 13, 2, 0, 11)]
    // 市況のみ（1 銘柄 1 要求）なら倍取れる
    [InlineData(300, 13, 1, 0, 23)]
    // 再試行のための余裕を引くと減る
    [InlineData(300, 13, 2, 40, 10)]
    // 巡回が増えれば銘柄数は減る
    [InlineData(300, 26, 2, 0, 5)]
    public void 日次上限から銘柄数上限を逆算する(
        int dailyLimit, int cycles, int perSymbol, int reserved, int expected)
    {
        FinnhubQuotaCalculator.MaxWatchlistSymbols(dailyLimit, cycles, perSymbol, reserved)
            .Should().Be(expected);
    }

    // 端数は**足りない側へ倒す**——超過はブロックを招き、収集が丸ごと止まる。
    [Fact]
    public void 端数は切り捨てる()
    {
        FinnhubQuotaCalculator.MaxWatchlistSymbols(25, cyclesPerDay: 13, requestsPerSymbolPerCycle: 2)
            .Should().Be(0, "26 要求/日 に満たないため 1 銘柄も監視できない");
    }

    [Fact]
    public void 余裕が上限を超えるなら0を返す()
    {
        FinnhubQuotaCalculator.MaxWatchlistSymbols(100, 13, 2, reservedRequests: 100).Should().Be(0);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(13, 0)]
    public void 巡回数と要求数は正でなければならない(int cycles, int perSymbol)
    {
        var act = () => FinnhubQuotaCalculator.MaxWatchlistSymbols(300, cycles, perSymbol);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
