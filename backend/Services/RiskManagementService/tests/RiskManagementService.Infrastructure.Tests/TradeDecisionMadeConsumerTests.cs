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
                // FR-20, #387, IADR-0148: 審査結果は段階ゲートの観測ログへ記録される（ハンドラの依存）。
                opts.Services.AddSingleton<IControlViolationObservationStore,
                    InMemoryControlViolationObservationStore>();
                // FR-19, #375, ADR-0021 決定3, IADR-0153: 口座種別の観測ストア（スナップショット組み立ての依存）。
                // 本テストの注文意図は内蔵 paper であり口座種別を要求しないため、観測は投入しない
                // （＝口座種別を確認できていない状態のまま検証する）。
                opts.Services.AddSingleton<IBrokerAccountObservationStore>(
                    new InMemoryBrokerAccountObservationStore(TimeProvider.System));
                opts.Services.AddSingleton<PortfolioSnapshotBuilder>();
                opts.Services.AddSingleton(sp => new OrderScreeningService(
                    sp.GetRequiredService<IRiskSettingsStore>(),
                    sp.GetRequiredService<PortfolioSnapshotBuilder>(),
                    sp.GetRequiredService<ILockoutStore>(),
                    sp.GetRequiredService<IClock>(),
                    sp.GetRequiredService<IBusinessCalendar>(),
                    // #428: 推定台帳は必須依存。本テストは強制買戻しを関心に持たないため空の台帳を渡す。
                    new InMemoryBuyInInferenceStore(),
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

    // FR-20, FR-11, #387, IADR-0148: **審査結果がメッセージ経路で観測ログへ記録される**（配線の確認）。
    // 記録が起きなければ集計は永久に未供給のままであり、Stage 1 → 2 は昇格できない（fail-safe だが不発）。
    // 発注先は算入対象（moomoo SIMULATE）とし、拒否理由はクラス B（緊急停止）＝件数は増えないが供給は作る。
    [Fact]
    public async Task 審査結果は観測ログへ記録される()
    {
        var killSwitch = new InMemoryKillSwitchStore();
        killSwitch.SetState(new KillSwitchState(true, "user", "停止", DateTimeOffset.UtcNow));
        using var host = await BuildHostAsync(killSwitch);
        var violations = host.Services.GetRequiredService<IControlViolationObservationStore>();
        violations.GetTally().Should().BeNull("観測が入るまでは未供給である");

        var intent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 10, 1_000m);
        await host.TrackActivity().InvokeMessageAndWaitAsync(
            new TradeDecisionMade(Guid.NewGuid(), intent, "判断", DateTimeOffset.UtcNow));

        var tally = violations.GetTally();
        tally.Should().NotBeNull("審査が動けば集計は供給される");
        tally!.Count.Should().Be(0, "緊急停止による拒否はクラス B であり統制違反に計上しない");

        await host.StopAsync();
    }

    // 全統制を通過する健全な運用状態（資金 10 万・損益ゼロ）。
    private sealed class HealthyPortfolioProvider : IPortfolioStateProvider
    {
        public PortfolioState GetCurrent() => new() { Capital = 100_000m };
    }
}
