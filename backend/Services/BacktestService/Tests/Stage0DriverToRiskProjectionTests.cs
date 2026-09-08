extern alias RiskManagementWorker;

using BacktestService.Domain;
using BacktestService.Features.Backtest;
using BacktestService.Hosted;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using RiskManagementWorker::RiskManagementService.Common.Abstractions;
using RiskManagementWorker::RiskManagementService.Domain;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement;
using RiskManagementWorker::RiskManagementService.Infrastructure.Persistence;
using RiskManagementWorker::RiskManagementService.Infrastructure.Steps;

namespace BacktestService.Tests;

// FR-15, FR-20, UC-06, ADR-0008, #688, IADR-0089, IADR-0310: 駆動 → 発行 → リスク管理の射影 → 昇格ゲートまでの通し。
//
// 実ブローカは使わない（発行側は stub 転送で捕捉し、捕捉した**そのメッセージ**を受け側ハンドラへ流す）。
// 実 RabbitMQ を介した疎通は #82 に残る（IADR-0310 決定5）。
//
// 🔴 見ているのは「経路が通ること」と同時に「**通っても昇格しないこと**」である。
public class Stage0DriverToRiskProjectionTests
{
    private const string ServiceName = "ai-stock-trading.backtest-service";
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task 駆動が発行したverdictはリスク管理へ射影され昇格を止め続ける()
    {
        // --- 発行側: 駆動を 1 巡回させ、実際に外へ出たメッセージを捕捉する ---
        using var publisher = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IHistoricalBarSource>(new EmptyBarSource());
                opts.Discovery.DisableConventionalDiscovery();
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

        var driver = new Stage0EvaluationService(
            publisher.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new Stage0EvaluationOptions
            {
                Enabled = true,
                Symbols = [new Stage0EvaluationOptions.SymbolEntry { Symbol = "AAPL", Market = Market.UnitedStates }],
            }),
            new FixedTimeProvider(Now),
            NullLogger<Stage0EvaluationService>.Instance);

        var publishSession = await publisher.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => driver.RunOnceAsync(CancellationToken.None));
        var verdict = publishSession.Sent.MessagesOf<BacktestEvaluated>().Should().ContainSingle().Which;
        await publisher.StopAsync();

        // --- 受け側: 段階別実績への射影と、昇格ゲートの拒否が続くことを見る ---
        var performance = new InMemoryStagePerformanceStore();
        var ledger = new InMemoryStageGateStore(TradingStage.Stage0Verification);
        var clock = new FixedClock(Now);
        var stageGate = new StageGateService(
            ledger,
            performance,
            new InMemoryControlViolationObservationStore(),
            new InMemoryStage1FillObservationStore(),
            new InMemoryStage1TradingDayObservationStore(),
            TradingDefaults.CreateStagePolicy(),
            new InMemoryRiskSettingsStore(),
            new KillSwitchService(new InMemoryKillSwitchStore(), new InMemorySettingsChangeLog(), clock),
            new ShortSellReleaseSourceInventory([]),
            clock);

        // 供給前: fail-safe 既定（BacktestPassed=false）で昇格は拒否される。
        stageGate.RequestTransition(TradingStage.Stage1Simulate, approver: "owner")
            .RejectionReasons.Should().Contain(StageGateCriterion.BacktestNotPassed);

        using var consumer = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IStagePerformanceStore>(performance);
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<BacktestEvaluatedProjectionHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

        var consumeSession = await consumer.TrackActivityForTest().InvokeMessageAndWaitAsync(verdict);
        consumeSession.Executed.MessagesOf<BacktestEvaluated>().Should().NotBeEmpty();

        // 射影されている（経路は通った）。
        var current = performance.GetCurrent();
        current.BacktestStrategyId.Should().Be(PlaceholderStrategy.StrategyId);
        current.BacktestIncludesShortSelling.Should().BeFalse();

        // 🔴 **否定形**: プレースホルダの verdict は本番の合否として記録されない＝昇格は止まったままである。
        current.BacktestPassed.Should().BeFalse();
        var after = stageGate.RequestTransition(TradingStage.Stage1Simulate, approver: "owner");
        after.Accepted.Should().BeFalse();
        after.RejectionReasons.Should().Contain(StageGateCriterion.BacktestNotPassed);
        ledger.Load().CurrentStage.Should().Be(TradingStage.Stage0Verification);

        await consumer.StopAsync();
    }

    // 過去データ源の最小の偽装（既定 provider（none）と同じく 1 本も返さない）。
    private sealed class EmptyBarSource : IHistoricalBarSource
    {
        public Task<HistoricalBarLoad> LoadBarsAsync(
            IReadOnlyList<(string Symbol, Market Market)> symbols,
            DateOnly from,
            DateOnly to,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoricalBarLoad([], []));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;

        public DateOnly Today => DateOnly.FromDateTime(now.UtcDateTime);
    }
}
