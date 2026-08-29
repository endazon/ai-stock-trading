using MarketMonitorService.Features.MarketMonitor;
using MarketMonitorService.Infrastructure.Persistence;
using MarketMonitorService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace MarketMonitorService.Tests;

// FR-03, UC-02: TradeDecisionMade 購読で基準値が判断時点価格へ更新されることを検証する。
//
// ADR-0013, IADR-0129, #354: MassTransit のテストハーネス（AddMassTransitTestHarness + harness.Consumed）から
// Wolverine.Tracking（TrackActivity + session.Executed）へ移行した。表明の意味は同じ
// （メッセージを流し、ハンドラが実行され、基準値が判断時点価格になる）。
public class TradeDecisionMadeBaselineConsumerTests
{
    [Fact]
    public async Task 判断確定で対象銘柄の基準値を判断時点価格へ更新する()
    {
        var baselines = new InMemoryPriceBaselineStore();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IPriceBaselineStore>(baselines);
                opts.Discovery.IncludeAssembly(typeof(TradeDecisionMadeBaselineHandler).Assembly);
                // 実ブローカへ接続しない（ローカル・CI ともに RabbitMQ を要求しない）。
                opts.StubAllExternalTransports();
            })
            .StartAsync();

        var intent = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.InternalPaper, 10, 1_234m);
        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new TradeDecisionMade(Guid.NewGuid(), intent, "判断", DateTimeOffset.UtcNow));

        session.Executed.MessagesOf<TradeDecisionMade>().Should().NotBeEmpty();
        baselines.GetBaseline("AAPL", Market.UnitedStates).Should().Be(1_234m);

        await host.StopAsync();
    }
}
