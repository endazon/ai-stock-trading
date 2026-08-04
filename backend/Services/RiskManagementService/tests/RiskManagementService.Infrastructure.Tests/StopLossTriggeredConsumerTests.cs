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
using Xunit;

namespace AiStockTrading.RiskManagement.Infrastructure.Tests;

// FR-10, FR-03, ADR-0003, IADR-0015: StopLossTriggered 購読 → LLM 迂回で Close の OrderApproved 発行を検証する。
public class StopLossTriggeredConsumerTests
{
    private const string ServiceName = "ai-stock-trading.risk-management-service";

    // ADR-0013, IADR-0129, #354: MassTransit のテストハーネスから Wolverine.Tracking へ移行した。
    // 明示登録（AddConsumer<T>）は「規約発見を止めて対象型だけを含める」形へ写す
    // （テストの対象範囲を旧テストと同一に保つ）。実ブローカへは接続しない。
    private static Task<IHost> BuildHostAsync(IKillSwitchStore? killSwitch = null) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton<IRiskSettingsStore, InMemoryRiskSettingsStore>();
                opts.Services.AddSingleton<StopLossExecutionService>();
                // kill switch は損切り執行に影響しない（無条件）ことを示すため、任意で起動状態を注入する。
                if (killSwitch is not null)
                {
                    opts.Services.AddSingleton(killSwitch);
                }

                // 本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す。
                // ルーティングを入れないと発行先が 1 つも無く、送信そのものが起きない。
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<StopLossTriggeredHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static StopLossTriggered Triggered() =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 10, 950m, 970m, DateTimeOffset.UtcNow);

    [Fact]
    public async Task 損切りイベントで決済のOrderApprovedを発行する()
    {
        using var host = await BuildHostAsync();

        var session1 = await host.TrackActivity().InvokeMessageAndWaitAsync(Triggered());

        session1.Executed.MessagesOf<StopLossTriggered>().Should().NotBeEmpty();
        session1.Sent.MessagesOf<OrderApproved>().Should().NotBeEmpty();
        var approved = session1.Sent.MessagesOf<OrderApproved>().First();
        approved.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        approved.Intent.Side.Should().Be(TradeSide.Sell);

        await host.StopAsync();
    }

    [Fact]
    public async Task kill_switch_起動中でも損切りは無条件に発行される()
    {
        // ADR-0003: 損切りは kill switch 起動中でも必ず実行する。
        var killSwitch = new InMemoryKillSwitchStore();
        killSwitch.SetState(new KillSwitchState(true, "user", "緊急停止", DateTimeOffset.UtcNow));
        using var host = await BuildHostAsync(killSwitch);

        var session1 = await host.TrackActivity().InvokeMessageAndWaitAsync(Triggered());

        session1.Sent.MessagesOf<OrderApproved>().Should().NotBeEmpty();

        await host.StopAsync();
    }
}
