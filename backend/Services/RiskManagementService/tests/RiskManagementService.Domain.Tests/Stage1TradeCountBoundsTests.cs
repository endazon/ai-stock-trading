using RiskManagementService.Domain;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Domain.Tests;

// FR-20, FR-13, SC-02, #423, 06_daytrading-review §4.1 条件 3 / §4.3, IADR-0164 決定5・決定6:
// **Stage 1 の最小取引件数を設定値にしたことの退行防止。**
//
// 裁定（質問票 第 13 回 Q6 の追加指示）が定めたのは次の 3 点であり、いずれも**取り違えると統制が緩む**。
//   1. 値域は 1〜1000（0 は不可・1001 は不可）
//   2. 100 件未満は**警告の対象**であり、**拒否の対象ではない**
//   3. 読めない値（旧行・値域外）は**既定 100**へ落とす（少ない件数＝合格に近い側へ倒さない）
public class Stage1TradeCountBoundsTests
{
    // ------------------------------------------------------------------
    // 値域の境界（0 不可 / 1 可 / 1000 可 / 1001 不可）
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(999)]
    [InlineData(1000)]
    public void 値域内の件数は受理される(int value)
    {
        Stage1TradeCountBounds.Validate(value).Should().BeNull();
    }

    // **0 以下は条件 3 が無条件に成立する。** 期間（60 営業日）だけで昇格できてしまい、
    // 「運用に足るかを統計的に判断できる」という条件 3 の存在意義が消える。
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ゼロ以下の件数は受理しない(int value)
    {
        Stage1TradeCountBounds.Validate(value).Should().NotBeNull();
    }

    // 上限 1000 は計画が定めた値である（§4.1）。超えると Stage 1 が事実上終わらない。
    [Theory]
    [InlineData(1001)]
    [InlineData(10_000)]
    [InlineData(int.MaxValue)]
    public void 上限1000を超える件数は受理しない(int value)
    {
        Stage1TradeCountBounds.Validate(value).Should().NotBeNull();
    }

    [Fact]
    public void 値域外はThrowIfOutOfRangeが例外を投げる()
    {
        var act = () => Stage1TradeCountBounds.ThrowIfOutOfRange(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 値域内はThrowIfOutOfRangeが例外を投げない()
    {
        var act = () => Stage1TradeCountBounds.ThrowIfOutOfRange(Stage1TradeCountBounds.Default);
        act.Should().NotThrow();
    }

    // ------------------------------------------------------------------
    // 既定値（計画の確定値 100 から動かさない）
    // ------------------------------------------------------------------

    // §4.1 条件 3・§4.3 の統計的根拠そのものである。**既定を下げる変更は計画の改訂を要する。**
    [Fact]
    public void 既定は100件であり統計的根拠の下限と一致する()
    {
        Stage1TradeCountBounds.Default.Should().Be(100);
        Stage1TradeCountBounds.StatisticalBasis.Should().Be(100);
        Stage1GateCriteria.Default.MinimumTradeCount.Should().Be(100);
    }

    // ------------------------------------------------------------------
    // 統計的根拠の警告（100 未満で必ず立ち、100 以上では立たない）
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(99)]
    public void 統計的根拠の下限を下回る件数は警告として宣言される(int value)
    {
        Stage1TradeCountBounds.BelowStatisticalBasis(value).Should().BeTrue();
        new Stage1GateCriteria(60, value, 120).BelowStatisticalBasis.Should().BeTrue();
    }

    // 逆方向の否定形。**満たしているのに警告を出すと、警告が常時出て誰も読まなくなる。**
    [Theory]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(1000)]
    public void 統計的根拠を満たす件数では警告を宣言しない(int value)
    {
        Stage1TradeCountBounds.BelowStatisticalBasis(value).Should().BeFalse();
        new Stage1GateCriteria(60, value, 120).BelowStatisticalBasis.Should().BeFalse();
    }

    // **最重要の否定形。** 裁定は「警告は設定を妨げない」と定める。警告の対象（100 未満）でも
    // **値域内であるかぎり受理**されなければならない。ここが赤くなる実装は
    // 「ついでに拒否する」へすり替わったことを意味する。
    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(99)]
    public void 統計的根拠を満たさない件数でも設定は妨げられない(int value)
    {
        Stage1TradeCountBounds.BelowStatisticalBasis(value).Should().BeTrue("警告の対象である");
        Stage1TradeCountBounds.Validate(value).Should().BeNull("しかし設定は妨げない");
    }

    // ------------------------------------------------------------------
    // 永続行からの解決（読めない値は「厳しい側」＝既定 100 へ倒す）
    // ------------------------------------------------------------------

    [Fact]
    public void 本項目を持たない旧行は既定100として読む()
    {
        Stage1TradeCountBounds.Resolve(null).Should().Be(100);
    }

    // **緩い側（少ない件数）へ倒れてはならない。** 値域外の値を「そのまま」または「下限 1」へ
    // 丸めると、手編集や不整合な行が擬似的に合格へ近づく。
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1001)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void 値域外の永続値は既定100へ落とす(int persisted)
    {
        Stage1TradeCountBounds.Resolve(persisted).Should().Be(100);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(1000)]
    public void 値域内の永続値はそのまま読む(int persisted)
    {
        Stage1TradeCountBounds.Resolve(persisted).Should().Be(persisted);
    }
}
