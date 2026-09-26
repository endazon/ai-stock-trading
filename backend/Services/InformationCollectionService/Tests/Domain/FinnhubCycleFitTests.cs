using InformationCollectionService.Domain;
using AwesomeAssertions;
using Xunit;

namespace InformationCollectionService.Tests;

// T-10-1460, FR-01, #1015, IADR-0435（計画 ADR-0043 決定2 (b)）: 1 巡回の Finnhub の要求が巡回間隔に収まる銘柄数。
// 1 巡回の要求数 ÷ 自制レート ≤ 巡回間隔 ⇔ 銘柄数 ≤ floor(自制レート × 巡回間隔 ÷ 1 銘柄あたりの要求数)。
public class FinnhubCycleFitTests
{
    [Theory]
    // 経路 B の既定（30 回/分・300 秒・現在値だけ）: 30 × 5 分 ÷ 1 = 150 銘柄。
    [InlineData(30, 300, 1, 150)]
    // 現在値＋企業ニュース（1 銘柄 2 要求）なら半分。
    [InlineData(30, 300, 2, 75)]
    // 本番既定の巡回（1800 秒）。
    [InlineData(30, 1800, 2, 450)]
    // 境界: 5 回/分・60 秒なら 5 銘柄ちょうど（計画 ADR-0043 実測 7 の監視サービスの組と同じ算術）。
    [InlineData(5, 60, 1, 5)]
    // 端数は切り捨てる（59 秒では 5 回/分で 4.9 → 4）。収まらない側へ倒さない。
    [InlineData(5, 59, 1, 4)]
    // 1 分に満たない巡回で 1 銘柄 2 要求なら、1 回/分では 0 銘柄しか収まらない。
    [InlineData(1, 30, 2, 0)]
    public void 収まる銘柄数は自制レートと巡回間隔と1銘柄あたりの要求数から切り捨てで決まる(
        int ratePerMinute, int intervalSeconds, int requestsPerSymbol, int expected)
    {
        FinnhubCycleFit.MaxSymbolsPerCycle(ratePerMinute, intervalSeconds, requestsPerSymbol).Should().Be(expected);
    }

    [Fact]
    public void 自制レートと巡回間隔の0以下は下限1として扱う()
    {
        // バケット（容量の下限 1）とポーラ（間隔の下限 1 秒）と同じ下限。0 除算・負の上限を作らない。
        FinnhubCycleFit.MaxSymbolsPerCycle(0, 0, 1).Should().Be(0);
        FinnhubCycleFit.MaxSymbolsPerCycle(-5, 120, 1).Should().Be(2);
    }

    [Fact]
    public void 一銘柄あたりの要求数が0以下は拒否する()
    {
        var act = () => FinnhubCycleFit.MaxSymbolsPerCycle(30, 300, 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 大きな値でも桁あふれしない()
    {
        FinnhubCycleFit.MaxSymbolsPerCycle(int.MaxValue, int.MaxValue, 1).Should().Be(int.MaxValue);
    }
}
