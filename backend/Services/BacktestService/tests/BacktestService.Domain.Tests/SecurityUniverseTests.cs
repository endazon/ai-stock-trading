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

    // FR-15, #208, IADR-0105: 実データ源の取得対象を PIT ユニバースから導出するための期間版。
    [Fact]
    public void 期間内に構成銘柄だった銘柄を返す()
    {
        // 期間中に上場廃止した BBB・期間中に上場した CCC も取得対象に含める（現存銘柄だけを取りに行かない）。
        var members = Universe().MembersBetween(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31));

        members.Should().BeEquivalentTo(
        [
            ("AAA", Market.UnitedStates),
            ("BBB", Market.UnitedStates),
            ("CCC", Market.UnitedStates),
        ]);
    }

    [Fact]
    public void 期間外の銘柄は取得対象に含めない()
    {
        // BBB は 2020-06-01 に廃止済み。CCC は 2020-03-01 上場のため両方とも含まれる期間を外す。
        var members = Universe().MembersBetween(new DateOnly(2020, 6, 1), new DateOnly(2020, 12, 31));

        members.Should().BeEquivalentTo([("AAA", Market.UnitedStates), ("CCC", Market.UnitedStates)]);
    }

    [Fact]
    public void 期間の端で構成だった銘柄を含める()
    {
        // 期間 = 廃止日前日のみ。半開区間 [ListedFrom, DelistedOn) の端を取りこぼさない。
        Universe().MembersBetween(new DateOnly(2020, 5, 31), new DateOnly(2020, 5, 31))
            .Should().Contain(("BBB", Market.UnitedStates));
    }

    [Fact]
    public void 開始日が終了日より後なら空を返す()
    {
        Universe().MembersBetween(new DateOnly(2020, 12, 31), new DateOnly(2020, 1, 1)).Should().BeEmpty();
    }

    [Fact]
    public void 重複する構成期間があっても銘柄は一度だけ返す()
    {
        // 再上場（同一銘柄が複数の構成期間を持つ）でも取得対象は 1 回で足りる。
        var universe = new SecurityUniverse(
        [
            new UniverseMembership("DDD", Market.Japan, new DateOnly(2020, 1, 1), new DateOnly(2020, 3, 1)),
            new UniverseMembership("DDD", Market.Japan, new DateOnly(2020, 6, 1), null),
        ]);

        universe.MembersBetween(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31))
            .Should().ContainSingle().Which.Should().Be(("DDD", Market.Japan));
    }
}
