using OrderExecutionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-05, FR-20, ADR-0002, IADR-0056, IADR-0111: 実弾（live）階層は「型として表現できるが到達不能」であることを固定する。
//
// 本テストは実弾解禁の可否そのものを主張する。LiveTradingReleased が誤って true になれば
// 「未解禁であること」を主張する下記テストが赤くなり、解禁が意図的な決定であることを強制する。
public class LiveTradingGateTests
{
    [Fact]
    public void 実弾は未解禁である()
    {
        // ★ 解禁は本 const を true にする 1 ファイルの変更に集約される（別 IADR＋IADR-0056 §3 の前提充足が要る）。
        LiveTradingGate.LiveTradingReleased.Should().BeFalse();
    }

    [Fact]
    public void live選択は停止し_解禁前提を告知する()
    {
        var act = () => LiveTradingGate.Ensure(BrokerSelection.Parse("moomoo", "live"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IADR-0056*").Which.Message.Should().Contain("moomoo-live");
    }

    // 🔴 T-15-122, FR-15, FR-20, ADR-0014 決定3, ADR-0054 決定3・4, #204 C-8, #1196, IADR-0498（受け入れ基準 3）:
    // 解禁前提の一覧（閂 0 の告知文）に「両層の組での Stage 0 合格」が項目として並ぶ。解禁の手順で人が読む一覧から欠けさせない。
    // 閂そのものは未解禁のまま（上の `実弾は未解禁である`）。
    [Fact]
    public void live選択の告知は両層の組でのStage0合格を解禁前提に含む()
    {
        var act = () => LiveTradingGate.Ensure(BrokerSelection.Parse("moomoo", "live"));

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain(LiveTradingGate.StageZeroTwoTierPrerequisite);
        LiveTradingGate.StageZeroTwoTierPrerequisite.Should()
            .Contain("Stage 0 合格").And.Contain("claude-haiku-4-5").And.Contain("claude-sonnet-5").And.Contain("両層");
        // 既存の 3 前提は消さない（足すだけ）。
        message.Should().Contain("Vault").And.Contain("#141").And.Contain("TradingDefaults");
        LiveTradingGate.LiveTradingReleased.Should().BeFalse();
    }

    [Theory]
    [InlineData("paper", null)]
    [InlineData("paper", "sim")]
    [InlineData("moomoo", "sim")]
    public void sim階層は素通しする_現行のペーパー_SIMULATE_運用を妨げない(string provider, string? environment)
    {
        var act = () => LiveTradingGate.Ensure(BrokerSelection.Parse(provider, environment));

        act.Should().NotThrow();
    }
}
