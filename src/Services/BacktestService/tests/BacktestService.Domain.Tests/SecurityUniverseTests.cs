using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Domain.Tests;

// FR-15, ADR-0008, 06_daytrading-review §3.2: 生存者バイアスのない銘柄ユニバース。
// Point-in-Time メンバーシップ（当時上場・後に廃止された銘柄を含む）を検証する。
public class SecurityUniverseTests
{
    private static readonly UniverseMembership Alive =
        new("AAA", Market.UnitedStates, new DateOnly(2020, 1, 1), null);

    // 2020-06-01 に上場廃止（＝当日以降は非構成）。
    private static readonly UniverseMembership Delisted =
        new("BBB", Market.UnitedStates, new DateOnly(2020, 1, 1), new DateOnly(2020, 6, 1));

    // 2020-03-01 に上場（＝当日から構成）。
    private static readonly UniverseMembership LateListed =
        new("CCC", Market.UnitedStates, new DateOnly(2020, 3, 1), null);

    private static SecurityUniverse Universe() => new([Alive, Delisted, LateListed]);

    [Fact]
    public void 上場前の銘柄は構成に含まれない()
    {
        Universe().MembersAsOf(new DateOnly(2020, 2, 1))
            .Should().NotContain(("CCC", Market.UnitedStates));
    }

    [Fact]
    public void 上場日から構成に含まれる()
    {
        Universe().MembersAsOf(new DateOnly(2020, 3, 1))
            .Should().Contain(("CCC", Market.UnitedStates));
    }

    [Fact]
    public void 上場廃止直前は構成に含まれる_生存者バイアス排除()
    {
        // 廃止日前日は「当時上場していた」ため含まれる（現存銘柄のみで検証しない）。
        Universe().MembersAsOf(new DateOnly(2020, 5, 31))
            .Should().Contain(("BBB", Market.UnitedStates));
    }

    [Fact]
    public void 上場廃止日以降は構成に含まれない()
    {
        Universe().MembersAsOf(new DateOnly(2020, 6, 1))
            .Should().NotContain(("BBB", Market.UnitedStates));
    }

    [Fact]
    public void 上場廃止のない銘柄は常に構成に含まれる()
    {
        Universe().MembersAsOf(new DateOnly(2025, 1, 1))
            .Should().Contain(("AAA", Market.UnitedStates));
    }
}
