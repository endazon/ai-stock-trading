using RiskManagementService.Domain;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// T-10-2310, FR-10, #1176, IADR-0495 決定1・2: 新規建ての最小の名目額（equity × しきい値）の純関数。
// 「1% 未満は見送り」——ちょうど 1% は通す。しきい値 0 は統制を外す。LLM の前の下界は「上限（1 注文・残枠の最小）が最小に届かない」。
public class MinimumEntryNotionalTests
{
    [Theory]
    [InlineData("999.99", true)]
    [InlineData("1000", false)]
    [InlineData("1000.01", false)]
    public void T_10_2310_equityの1パーセント未満だけが最小を割る(string notional, bool below) =>
        MinimumEntryNotional.IsBelow(decimal.Parse(notional, System.Globalization.CultureInfo.InvariantCulture), 100_000m, 0.01m)
            .Should().Be(below);

    // issue の実例: AAPL 13 株 @334.11 ＝ 4,343.43（equity 約 970,000 の約 0.45%）。
    [Fact]
    public void T_10_2310_AAPLの13株は最小を割る()
    {
        MinimumEntryNotional.IsBelow(13 * 334.11m, 970_000m, 0.01m).Should().BeTrue();
        MinimumEntryNotional.MinimumFor(970_000m, 0.01m).Should().Be(9_700m);
    }

    [Fact]
    public void T_10_2310_しきい値0は統制を外す()
    {
        MinimumEntryNotional.IsBelow(0.01m, 970_000m, 0m).Should().BeFalse();
        MinimumEntryNotional.CapacityCannotReach(970_000m, 242_500m, 0m, 0m).Should().BeFalse();
    }

    // 上限は 1 注文上限と残枠の小さい方。どちらかが最小に届かなければ真。ちょうど最小なら偽（ちょうど 1% の新規建てはあり得る）。
    [Theory]
    [InlineData(25_000, 999, true)]
    [InlineData(25_000, 1_000, false)]
    [InlineData(999, 50_000, true)]
    [InlineData(25_000, 0, true)]
    public void T_10_2310_上限が最小に届かなければLLMの前に分かる(int maxOrder, int available, bool cannotReach) =>
        MinimumEntryNotional.CapacityCannotReach(100_000m, maxOrder, available, 0.01m).Should().Be(cannotReach);

    [Theory]
    [InlineData("0")]
    [InlineData("0.01")]
    [InlineData("0.25")]
    public void T_10_2311_範囲内のしきい値は受け付ける(string ratio)
    {
        var value = decimal.Parse(ratio, System.Globalization.CultureInfo.InvariantCulture);
        MinimumEntryNotional.Validate(value).Should().Be(value);
    }

    [Theory]
    [InlineData("-0.0001")]
    [InlineData("0.2501")]
    [InlineData("1")]
    public void T_10_2311_範囲外のしきい値は例外(string ratio)
    {
        var value = decimal.Parse(ratio, System.Globalization.CultureInfo.InvariantCulture);
        ((Action)(() => MinimumEntryNotional.Validate(value))).Should().Throw<ArgumentOutOfRangeException>();
    }
}
