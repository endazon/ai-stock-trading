using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace AiStockTrading.RiskManagement.Infrastructure.Tests;

// FR-20, FR-15, UC-06, IADR-0089: バックテスト verdict（BacktestEvaluated）を購読して段階別実績へ射影するハンドラを
// Wolverine のテストハーネス（Wolverine.Tracking）+ インメモリストアで検証する。運用系フィールド保全と fail-safe（昇格拒否）を担保する。
public class BacktestEvaluatedProjectionConsumerTests
{
    // ADR-0013, IADR-0129, #354: MassTransit のテストハーネスから Wolverine.Tracking へ移行した。
    // 明示登録（AddConsumer<T>）は「規約発見を止めて対象型だけを含める」形へ写す
    // （テストの対象範囲を旧テストと同一に保つ）。実ブローカへは接続しない。
    private static Task<IHost> BuildHostAsync(IStagePerformanceStore store) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(store);
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<BacktestEvaluatedProjectionHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static BacktestEvaluated Verdict(bool passed, decimal maxDd) =>
        new(passed, maxDd, DeflatedSharpe: 1.2, ProbabilityOfBacktestOverfitting: 0.1,
            FailedChecks: passed ? string.Empty : "DeflatedSharpe", DateTimeOffset.UtcNow);

    [Fact]
    public async Task 合格verdictを段階別実績へ射影し昇格を解錠する()
    {
        var store = new InMemoryStagePerformanceStore();
        // 供給前は既定（BacktestPassed=false）＝昇格拒否の fail-safe。
        store.GetCurrent().BacktestPassed.Should().BeFalse();

        using var host = await BuildHostAsync(store);

        var session1 = await host.TrackActivity().InvokeMessageAndWaitAsync(Verdict(passed: true, maxDd: 0.08m));
        session1.Executed.MessagesOf<BacktestEvaluated>().Should().NotBeEmpty();

        var perf = store.GetCurrent();
        perf.BacktestPassed.Should().BeTrue();
        perf.BacktestMaxDrawdownRatio.Should().Be(0.08m);

        await host.StopAsync();
    }

    [Fact]
    public async Task 運用系フィールドは射影で温存する()
    {
        var store = new InMemoryStagePerformanceStore();
        // 別ドライバが供給済みの運用系フィールドを事前に設定する（backtest 由来ではない）。
        store.Save(new StagePerformance
        {
            ObservedMaxDrawdownRatio = 0.12m,
            Stage1QualifiedTradingDays = 60,
            Stage1TradeCount = 100,
            SlippageAndCostWithinExpected = true,
            DailyLossLimitRespected = true,
        });

        using var host = await BuildHostAsync(store);

        var session1 = await host.TrackActivity().InvokeMessageAndWaitAsync(Verdict(passed: true, maxDd: 0.05m));
        session1.Executed.MessagesOf<BacktestEvaluated>().Should().NotBeEmpty();

        var perf = store.GetCurrent();
        // backtest 由来は更新される。
        perf.BacktestPassed.Should().BeTrue();
        perf.BacktestMaxDrawdownRatio.Should().Be(0.05m);
        // 運用系は温存（別ドライバの供給源を上書きしない）。
        perf.ObservedMaxDrawdownRatio.Should().Be(0.12m);
        perf.Stage1QualifiedTradingDays.Should().Be(60);
        perf.Stage1TradeCount.Should().Be(100);
        perf.SlippageAndCostWithinExpected.Should().BeTrue();
        perf.DailyLossLimitRespected.Should().BeTrue();

        await host.StopAsync();
    }

    [Fact]
    public async Task 合格verdict供給後にStage0から1への昇格が受理される()
    {
        // #164 受け入れ基準 2（in-repo 分）: verdict 供給 → 段階別実績 → 昇格ゲートまでの通しを検証する。
        // 供給前は BacktestNotPassed で拒否され、供給後は同じ承認要求が受理される（BacktestNotPassed が解消する）。
        // 実 publish ホスト（BacktestService 側）と実コンテナ E2E は #82 に残す。
        var store = new InMemoryStagePerformanceStore();
        var ledger = new InMemoryStageGateStore(TradingStage.Stage0Verification);
        var clock = new FixedClock(new DateTimeOffset(2026, 7, 26, 9, 0, 0, TimeSpan.Zero));
        var stageGate = new StageGateService(
            ledger,
            store,
            new InMemoryControlViolationObservationStore(),
            new InMemoryStage1FillObservationStore(),
            TradingDefaults.CreateStagePolicy(),
            new KillSwitchService(new InMemoryKillSwitchStore(), new InMemorySettingsChangeLog(), clock),
            clock);

        // 供給前: fail-safe 既定で昇格は拒否される。
        var before = stageGate.RequestTransition(TradingStage.Stage1Simulate, approver: "owner");
        before.Accepted.Should().BeFalse();
        before.RejectionReasons.Should().Contain(StageGateCriterion.BacktestNotPassed);

        using var host = await BuildHostAsync(store);

        var session1 = await host.TrackActivity().InvokeMessageAndWaitAsync(Verdict(passed: true, maxDd: 0.08m));
        session1.Executed.MessagesOf<BacktestEvaluated>().Should().NotBeEmpty();

        // 供給後: 同じ承認要求が受理され、Stage 1 へ遷移する（昇格ゲートが解錠される）。
        var after = stageGate.RequestTransition(TradingStage.Stage1Simulate, approver: "owner");
        after.Accepted.Should().BeTrue();
        after.Transition!.Kind.Should().Be(StageTransitionKind.Promotion);
        ledger.Load().CurrentStage.Should().Be(TradingStage.Stage1Simulate);

        await host.StopAsync();
    }

    [Fact]
    public async Task 不合格verdictは昇格拒否を維持しつつ実DDを更新する()
    {
        var store = new InMemoryStagePerformanceStore();
        using var host = await BuildHostAsync(store);

        var session1 = await host.TrackActivity().InvokeMessageAndWaitAsync(Verdict(passed: false, maxDd: 0.30m));
        session1.Executed.MessagesOf<BacktestEvaluated>().Should().NotBeEmpty();

        var perf = store.GetCurrent();
        perf.BacktestPassed.Should().BeFalse();
        perf.BacktestMaxDrawdownRatio.Should().Be(0.30m);

        await host.StopAsync();
    }

    // 段階ゲートの遷移時刻を固定するための時計（本テストでは時刻自体は検証しない）。
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;

        public DateOnly Today => DateOnly.FromDateTime(now.UtcDateTime);
    }
}
