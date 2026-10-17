using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Kernel.Tests.Trading;

// FR-04, FR-07, #1384, IADR-0557 決定 1: 方針の「条件 X:」書式行だけから新規建ての条件の記号を読む（自由文は読まない）。
public class PolicyEntryConditionsTests
{
    // #1384 AC-04-20: 書式行（箇条書きの印・売買の向きの語・名前の括弧・全角）から記号を集める。
    [Fact]
    public void 書式行から条件の記号を集める()
    {
        const string policy = """
            条件 A: AAPL 前日比 -3% 以下で買い
            - 買い条件 B（急落反発）: 日中安値から +1% 戻したら買い
            ・売り条件Ｃ：保有の撤退
            * 新規建て条件 D (ブレイク): 高値更新で買い
            条件 A: 同じ記号の 2 行目
            """;

        PolicyEntryConditions.Symbols(policy).Should().BeEquivalentTo(["A", "B", "C", "D"]);
    }

    // #1384 AC-04-21（否定形）: 自由文・記号が 2 字以上・コロンの無い行・小文字は読まない。空の方針は空集合。
    [Theory]
    [InlineData("条件 B で建てた建玉は安値割れで撤退する。")]
    [InlineData("条件 AB: 2 字の記号")]
    [InlineData("条件 b: 小文字")]
    [InlineData("買い条件は B: 語順違い")]
    [InlineData("")]
    [InlineData(null)]
    public void 書式に合わない行からは記号を読まない(string? policy)
    {
        PolicyEntryConditions.Symbols(policy).Should().BeEmpty();
    }

    // #1384 AC-04-22: 判断が答えた記号を英大文字 1 字へ寄せる。寄せられなければ null。
    [Theory]
    [InlineData("B", "B")]
    [InlineData(" b ", "B")]
    [InlineData("Ｂ", "B")]
    [InlineData("BB", null)]
    [InlineData("1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void 答えた記号を英大文字1字へ寄せる(string? answered, string? expected)
    {
        PolicyEntryConditions.NormalizeSymbol(answered).Should().Be(expected);
    }
}
