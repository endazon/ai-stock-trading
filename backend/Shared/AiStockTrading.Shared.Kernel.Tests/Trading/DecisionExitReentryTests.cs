using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Kernel.Tests.Trading;

// FR-10, FR-15, #1176, IADR-0495 決定3, #1209, IADR-0507: 「判断由来の決済の後は、同じ取引日のうち同じ方向の新規建てをしない」の共有の述語。
// 本番の審査（リスク管理の射影が時刻を取引日へ写して委ねる）と Stage 0 の再生（判断日・約定日を渡す）が同じ規則を通る。
public class DecisionExitReentryTests
{
    private static readonly DateOnly Day = new(2026, 10, 6);

    private static DecisionExitOnTradingDays Exit(TradeSide side, DateOnly approvedOn, params DateOnly[] filledOn) =>
        new(side, approvedOn, filledOn);

    // T-10-2410: 承認または約定の取引日が当日なら、決済の方向に立つ（売りの決済＝ロング側・買いの決済＝ショート側）。決済が無ければ立たない。
    [Fact]
    public void T_10_2410_承認か約定が当日なら決済した建玉の方向に立つ()
    {
        DecisionExitReentry.Project([Exit(TradeSide.Sell, Day)], Day).Should().Be(new DecisionExitSides(true, false));
        DecisionExitReentry.Project([Exit(TradeSide.Buy, Day)], Day).Should().Be(new DecisionExitSides(false, true));
        DecisionExitReentry.Project([Exit(TradeSide.Sell, Day.AddDays(-1), Day)], Day)
            .Should().Be(new DecisionExitSides(true, false), "承認は前日でも約定が当日なら数える");
        DecisionExitReentry.Project([Exit(TradeSide.Sell, Day, Day.AddDays(-1), Day)], Day)
            .Should().Be(new DecisionExitSides(true, false), "部分約定（約定 2 行）も同じ");
        DecisionExitReentry.Project([], Day).Should().Be(new DecisionExitSides(false, false));
    }

    // T-10-2410: 🔴 否定形。承認・約定とも当日でなければ数えない（前日・翌日）。
    [Theory]
    [InlineData(-1, -1)]
    [InlineData(-2, -1)]
    [InlineData(1, 1)]
    public void T_10_2410_承認も約定も当日でなければ数えない(int approvedOffset, int filledOffset)
    {
        DecisionExitReentry.Project([Exit(TradeSide.Sell, Day.AddDays(approvedOffset), Day.AddDays(filledOffset))], Day)
            .Should().Be(new DecisionExitSides(false, false));
    }

    // T-10-2410: 同じ方向の新規建てだけを塞ぐ（ロングの決済は買いの新規建て、ショートの決済は売りの新規建て）。反対方向は止めない。
    [Theory]
    [InlineData(true, false, TradeSide.Buy, true)]
    [InlineData(true, false, TradeSide.Sell, false)]
    [InlineData(false, true, TradeSide.Sell, true)]
    [InlineData(false, true, TradeSide.Buy, false)]
    [InlineData(false, false, TradeSide.Buy, false)]
    [InlineData(true, true, TradeSide.Sell, true)]
    public void T_10_2410_同じ方向の新規建てだけを塞ぐ(bool longSide, bool shortSide, TradeSide entry, bool blocked)
    {
        DecisionExitReentry.BlocksEntry(longSide, shortSide, entry).Should().Be(blocked);
    }
}
