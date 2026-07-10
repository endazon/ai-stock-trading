using AiStockTrading.MarketMonitor.Application.Adapters;
using AiStockTrading.MarketMonitor.Application.Ports;
using AiStockTrading.MarketMonitor.Worker.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.MarketMonitor.Worker.Tests;

// FR-03, UC-02: TradeDecisionMade 購読で基準値が判断時点価格へ更新されることを検証する。
public class TradeDecisionMadeConsumerTests
{
    [Fact]
    public async Task 判断確定で対象銘柄の基準値を判断時点価格へ更新する()
    {
        var baselines = new InMemoryPriceBaselineStore();
        await using var provider = new ServiceCollection()
            .AddSingleton<IPriceBaselineStore>(baselines)
            .AddMassTransitTestHarness(x => x.AddConsumer<TradeDecisionMadeConsumer>())
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var intent = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, TradeMode.Paper, 10, 1_234m);
        await harness.Bus.Publish(new TradeDecisionMade(Guid.NewGuid(), intent, "判断", DateTimeOffset.UtcNow));

        (await harness.Consumed.Any<TradeDecisionMade>()).Should().BeTrue();
        baselines.GetBaseline("AAPL", Market.UnitedStates).Should().Be(1_234m);

        await harness.Stop();
    }
}
