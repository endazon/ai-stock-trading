using AiStockTrading.OrderExecution.Worker.Composable.Adapters;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.OrderExecution.Worker.Tests;

// FR-05, ADR-0002, IADR-0016: ブローカ選択の安全ゲート（実弾防止）の検証。
public class BrokerFactoryTests
{
    [Theory]
    [InlineData("paper")]
    [InlineData("Paper")]
    [InlineData(" PAPER ")]
    [InlineData(null)]
    [InlineData("")]
    public void 既定またはpaper指定はペーパーブローカを返す(string? provider)
    {
        BrokerFactory.Create(provider).Should().BeOfType<PaperBrokerAdapter>();
    }

    [Fact]
    public void moomoo指定は実弾防止のため起動時に停止する()
    {
        // ADR-0002 は Proposed・OpenD PoC 未了。実装せず選択で停止（実弾を撃たない）。
        var act = () => BrokerFactory.Create("moomoo");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*未実装*");
    }

    [Fact]
    public void 未知のプロバイダは停止する()
    {
        var act = () => BrokerFactory.Create("etrade");

        act.Should().Throw<InvalidOperationException>();
    }
}
