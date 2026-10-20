using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Kernel.Tests.Trading;

// T-10-2926, FR-04, FR-10, #1396, IADR-0561 決定 4: 方針の決まった書式の行（「閉場前の手仕舞い: 有効」「持ち越し可: …」）だけを読み、自由文を読まない。
public class PolicyPreCloseLinesTests
{
    [Theory]
    [InlineData("閉場前の手仕舞い: 有効")]
    [InlineData("- 閉場前の手仕舞い：有効")] // 全角コロン（NFKC）・箇条書き
    [InlineData("・閉場前の手仕舞い : 有効 ")]
    public void T_10_2926_有効の行を読む(string line)
    {
        PolicyPreCloseLines.FlattenEnabled($"方針\n{line}\n以上").Should().BeTrue();
    }

    [Theory]
    [InlineData("閉場前の手仕舞い: 無効")]
    [InlineData("閉場前の手仕舞い: する")]
    [InlineData("閉場前の手仕舞い: 有効にする")]
    [InlineData("保有は原則日中のみとし、取引終盤は新規建てを控えて手仕舞いを優先する")] // 稼働中の方針（版 5）の自由文
    [InlineData("注: 閉場前の手仕舞い: 有効")] // 行頭でない
    [InlineData("")]
    public void T_10_2926_有効以外の値や自由文は有効化として読まない(string line)
    {
        PolicyPreCloseLines.FlattenEnabled(line).Should().BeFalse();
    }

    [Fact]
    public void T_10_2926_方針が無ければ有効化しない()
    {
        PolicyPreCloseLines.FlattenEnabled(null).Should().BeFalse();
        PolicyPreCloseLines.OvernightAllowed(null).Should().BeEmpty();
    }

    [Fact]
    public void T_10_2926_持ち越し可の銘柄を区切りに依らず読む()
    {
        var policy = "持ち越し可: NVDA, aapl、MSFT・BRK.B / 7203\n- 持ち越し可：TSLA。";

        PolicyPreCloseLines.OvernightAllowed(policy)
            .Should().BeEquivalentTo(["NVDA", "AAPL", "MSFT", "BRK.B", "7203", "TSLA"]);
    }

    [Theory]
    [InlineData("NVDA は持ち越し可とする")] // 自由文
    [InlineData("持ち越しは原則しない: NVDA")]
    [InlineData("注: 持ち越し可: NVDA")]
    public void T_10_2926_書式の外の行から銘柄を拾わない(string line)
    {
        PolicyPreCloseLines.OvernightAllowed(line).Should().BeEmpty();
    }

    [Fact]
    public void T_10_2926_持ち越し可の行の中の銘柄でない語は拾わない()
    {
        PolicyPreCloseLines.OvernightAllowed("持ち越し可: NVDA（決算前）")
            .Should().BeEmpty("「NVDA（決算前）」は銘柄の書式に合わない（推測で NVDA を拾わない）");
    }
}
