using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiStockTrading.RiskManagement.Infrastructure.Tests;

// FR-10, UC-01, UC-02: TradeDecisionMade 購読 → 承認/拒否発行を Wolverine のテストハーネス
// （Wolverine.Tracking）で検証する。
public class TradeDecisionMadeConsumerTests
{
    private const string ServiceName = "ai-stock-trading.risk-management-service";

    private static OrderIntent Entry() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 1_000m);

    // ADR-0013, IADR-0129, #354: MassTransit のテストハーネスから Wolverine.Tracking へ移行した。
    // 明示登録（AddConsumer<T>）は「規約発見を止めて対象型だけを含める」形へ写す
    // （テストの対象範囲を旧テストと同一に保つ）。実ブローカへは接続しない。
    private static Task<IHost> BuildHostAsync(IKillSwitchStore killSwitch) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton<IBusinessCalendar, WeekendBusinessCalendar>();
                opts.Services.AddSingleton<IRiskSettingsStore, InMemoryRiskSettingsStore>();
                opts.Services.AddSingleton(killSwitch);
                opts.Services.AddSingleton<IPauseStore, InMemoryPauseStore>();
                opts.Services.AddSingleton<ILockoutStore, InMemoryLockoutStore>();
                opts.Services.AddSingleton<ISettingsChangeLog, InMemorySettingsChangeLog>();
                opts.Services.AddSingleton<IPortfolioStateProvider>(new HealthyPortfolioProvider());
                opts.Services.AddSingleton<PortfolioSnapshotBuilder>();
                opts.Services.AddSingleton(sp => new OrderScreeningService(
                    sp.GetRequiredService<IRiskSettingsStore>(),
                    sp.GetRequiredService<PortfolioSnapshotBuilder>(),
                    sp.GetRequiredService<ILockoutStore>(),
                    sp.GetRequiredService<IClock>(),
                    sp.GetRequiredService<IBusinessCalendar>(),
                    null));

                // 本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す。
                // ルーティングを入れないと発行先が 1 つも無く、送信そのものが起きない。
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<TradeDecisionMadeHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    [Fact]
    public async Task 承認された注文は_OrderApproved_を発行する()
    {
        using var host = await BuildHostAsync(new InMemoryKillSwitchStore());

        var session1 = await host.TrackActivity().InvokeMessageAndWaitAsync(new TradeDecisionMade(Guid.NewGuid(), Entry(), "判断", DateTimeOffset.UtcNow));

        session1.Executed.MessagesOf<TradeDecisionMade>().Should().NotBeEmpty();
        session1.Sent.MessagesOf<OrderApproved>().Should().NotBeEmpty();
        session1.Sent.MessagesOf<OrderRejected>().Should().BeEmpty();

        await host.StopAsync();
    }

    [Fact]
    public async Task kill_switch_起動中は_OrderRejected_を発行する()
    {
        var killSwitch = new InMemoryKillSwitchStore();
        killSwitch.SetState(new KillSwitchState(true, "user", "停止", DateTimeOffset.UtcNow));
        using var host = await BuildHostAsync(killSwitch);

        var session1 = await host.TrackActivity().InvokeMessageAndWaitAsync(new TradeDecisionMade(Guid.NewGuid(), Entry(), "判断", DateTimeOffset.UtcNow));

        session1.Sent.MessagesOf<OrderRejected>().Should().NotBeEmpty();
        var rejected = session1.Sent.MessagesOf<OrderRejected>().First();
        rejected.Reasons.Should().Contain(RejectionReason.KillSwitchActive);

        await host.StopAsync();
    }

    // 全統制を通過する健全な運用状態（資金 10 万・損益ゼロ）。
    private sealed class HealthyPortfolioProvider : IPortfolioStateProvider
    {
        public PortfolioState GetCurrent() => new() { Capital = 100_000m };
    }
}
