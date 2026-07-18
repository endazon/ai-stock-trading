using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.RiskManagement.Worker.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.RiskManagement.Worker.Tests;

// FR-20, FR-15, UC-06, IADR-0089: バックテスト verdict（BacktestEvaluated）を購読して段階別実績へ射影する Consumer を
// MassTransit テストハーネス + インメモリストアで検証する。運用系フィールド保全と fail-safe（昇格拒否）を担保する。
public class BacktestEvaluatedProjectionConsumerTests
{
    private static ServiceProvider BuildProvider(IStagePerformanceStore store) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton(store)
            .AddMassTransitTestHarness(x => x.AddConsumer<BacktestEvaluatedProjectionConsumer>())
            .BuildServiceProvider(true);

    private static BacktestEvaluated Verdict(bool passed, decimal maxDd) =>
        new(passed, maxDd, DeflatedSharpe: 1.2, ProbabilityOfBacktestOverfitting: 0.1,
            FailedChecks: passed ? string.Empty : "DeflatedSharpe", DateTimeOffset.UtcNow);

    [Fact]
    public async Task 合格verdictを段階別実績へ射影し昇格を解錠する()
    {
        var store = new InMemoryStagePerformanceStore();
        // 供給前は既定（BacktestPassed=false）＝昇格拒否の fail-safe。
        store.GetCurrent().BacktestPassed.Should().BeFalse();

        await using var provider = BuildProvider(store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(Verdict(passed: true, maxDd: 0.08m));
        (await harness.Consumed.Any<BacktestEvaluated>()).Should().BeTrue();

        var perf = store.GetCurrent();
        perf.BacktestPassed.Should().BeTrue();
        perf.BacktestMaxDrawdownRatio.Should().Be(0.08m);

        await harness.Stop();
    }

    [Fact]
    public async Task 運用系フィールドは射影で温存する()
    {
        var store = new InMemoryStagePerformanceStore();
        // 別ドライバが供給済みの運用系フィールドを事前に設定する（backtest 由来ではない）。
        store.Save(new StagePerformance
        {
            ObservedMaxDrawdownRatio = 0.12m,
            PaperDeviationExplained = true,
            ControlViolationCount = 3,
            SlippageAndCostWithinExpected = true,
            DailyLossLimitRespected = true,
        });

        await using var provider = BuildProvider(store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(Verdict(passed: true, maxDd: 0.05m));
        (await harness.Consumed.Any<BacktestEvaluated>()).Should().BeTrue();

        var perf = store.GetCurrent();
        // backtest 由来は更新される。
        perf.BacktestPassed.Should().BeTrue();
        perf.BacktestMaxDrawdownRatio.Should().Be(0.05m);
        // 運用系は温存（別ドライバの供給源を上書きしない）。
        perf.ObservedMaxDrawdownRatio.Should().Be(0.12m);
        perf.PaperDeviationExplained.Should().BeTrue();
        perf.ControlViolationCount.Should().Be(3);
        perf.SlippageAndCostWithinExpected.Should().BeTrue();
        perf.DailyLossLimitRespected.Should().BeTrue();

        await harness.Stop();
    }

    [Fact]
    public async Task 不合格verdictは昇格拒否を維持しつつ実DDを更新する()
    {
        var store = new InMemoryStagePerformanceStore();
        await using var provider = BuildProvider(store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(Verdict(passed: false, maxDd: 0.30m));
        (await harness.Consumed.Any<BacktestEvaluated>()).Should().BeTrue();

        var perf = store.GetCurrent();
        perf.BacktestPassed.Should().BeFalse();
        perf.BacktestMaxDrawdownRatio.Should().Be(0.30m);

        await harness.Stop();
    }
}
