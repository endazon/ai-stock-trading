using AwesomeAssertions;
using OrderExecutionService.Infrastructure.ExternalServices;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-11, #1148, IADR-0476: OpenD の retMsg から口座 ID の全桁を除く純関数（MMApiMoomooTradeClient.RedactRetMsg）。
// 偽 OpenD を通す経路の試験は結合試験（MoomooAdapterFakeOpenDIntegrationTests・T-10-2000〜T-10-2005, T-10-2007）。
public class MoomooRetMsgRedactionTests
{
    private const ulong SimAccId = 724808UL;
    private const ulong RealAccId = 284852705357372276UL;

    // 口座一覧で見た集合（順序は問わない）。
    private static readonly ulong[] Known = [SimAccId, RealAccId];

    // 🔴 T-10-2006, FR-11, #1148: 既知の口座 ID だけを末尾 2 桁以外伏せ、他の数字（注文 ID・回数）は残す。
    [Theory]
    [InlineData("acc 724808 denied", "acc ****08 denied")]
    [InlineData("real 284852705357372276 / sim 724808", "real ****76 / sim ****08")]
    [InlineData("order 9000000001 not found (acc 724808)", "order 9000000001 not found (acc ****08)")]
    [InlineData("Maximum 10 times per 30 seconds", "Maximum 10 times per 30 seconds")]
    [InlineData("acc=724808,724808", "acc=****08,****08")]
    public void 既知の口座IDだけを伏せ他の数字は残す(string retMsg, string expected)
    {
        MMApiMoomooTradeClient.RedactRetMsg(retMsg, Known).Should().Be(expected);
    }

    // 🔴 T-10-2006, FR-11, #1148: 口座一覧をまだ読めていない（集合が空）なら、検証口と同じく 6 桁以上の数字の並びを伏せる。
    [Theory]
    [InlineData("accounts 724808,284852705357372276 unavailable", "accounts ****08,****76 unavailable")]
    [InlineData("Maximum 10 times per 30 seconds", "Maximum 10 times per 30 seconds")]
    [InlineData("code 12345 / 123456", "code 12345 / ****56")]
    public void 口座が未確定なら6桁以上の数字の並びを伏せる(string retMsg, string expected)
    {
        MMApiMoomooTradeClient.RedactRetMsg(retMsg, []).Should().Be(expected);
    }

    // T-10-2006, FR-11, #1148: 長い ID の中に短い ID が含まれても、長い方を先に伏せる（短い方の伏せで長い方の頭が残らない）。
    [Fact]
    public void 長い口座IDを先に伏せる()
    {
        // 集合は小さい順で渡す（並べ替えは RedactRetMsg の責務）。
        MMApiMoomooTradeClient.RedactRetMsg("acc 99724808 / 724808", [SimAccId, 99724808UL])
            .Should().Be("acc ****08 / ****08");
    }

    // T-10-2006, FR-11, #1148: 並びは数値の大きい順（文字列の辞書順では "1724808" < "724808" となり、短い方を先に伏せて
    // 長い方の頭 1 桁が残る。独立監査 🟢: 辞書順への変異が生き残っていた）。
    [Fact]
    public void 辞書順では短い方が先になる組でも長い口座IDを先に伏せる()
    {
        MMApiMoomooTradeClient.RedactRetMsg("acc 1724808 / 724808", [724808UL, 1724808UL])
            .Should().Be("acc ****08 / ****08");
    }

    // T-10-2006, FR-11, #1148: 空・null の retMsg は空のまま（例外文の形を変えない）。
    [Fact]
    public void 空のretMsgは空のまま()
    {
        MMApiMoomooTradeClient.RedactRetMsg(null, Known).Should().BeEmpty();
        MMApiMoomooTradeClient.RedactRetMsg(string.Empty, []).Should().BeEmpty();
    }
}
