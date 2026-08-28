using OrderExecutionService.Infrastructure.Adapters;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Infrastructure.Tests;

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
