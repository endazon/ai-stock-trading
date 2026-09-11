extern alias RiskManagementWorker;

using BacktestService.Domain;
using BacktestService.Features.Backtest;
using BacktestService.Features.Backtest.EvaluateStage0Gate;
using BacktestService.Hosted;
using AiStockTrading.Shared.Contracts.Backtest;
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
        // 🔴 #632, IADR-0329 決定3: この経路はバー 0 本で**判定を走らせていない**ため戦略を名乗らない。
        // 空文字は `StagePerformance.BacktestStrategyId` の既定と同値＝戦略の同一性を名乗れない状態である。
        var current = performance.GetCurrent();
        current.BacktestStrategyId.Should().BeEmpty();
        current.BacktestIncludesShortSelling.Should().BeFalse();

        // 🔴 **否定形**: 走らせていない verdict は本番の合否として記録されない＝昇格は止まったままである。
        current.BacktestPassed.Should().BeFalse();
        var after = stageGate.RequestTransition(TradingStage.Stage1Simulate, approver: "owner");
        after.Accepted.Should().BeFalse();
        after.RejectionReasons.Should().Contain(StageGateCriterion.BacktestNotPassed);
        ledger.Load().CurrentStage.Should().Be(TradingStage.Stage0Verification);

        await consumer.StopAsync();
    }

    // FR-04, FR-15, FR-20, ADR-0008, ADR-0033, #632, IADR-0318 / IADR-0329:
    // **本番戦略（AI 判断の記録・再生）**での通し。上のテストが「評価できない側」を見るのに対し、
    // 本テストは**本物の判定器（Stage0GateService）まで到達した verdict** が射影されることを見る。
    //
    // 🔴 見ているのは「本番戦略で経路が通ること」と同時に「**通っても昇格しないこと**」である。
    //
    // 🔴 **［2026-09-11 変更 / #777・ADR-0039・IADR-0337］** 旧記述「試行数（最小 20）で落ちる。これは
    // 仕様である」は**もはや成立しない** —— 記録再生は探索を持たないため **PBO は `評価不能`** であり、
    // 試行数の下限は適用されない。**合否は残る条件で決まる**（この記録では DSR ほかで落ちる）。
    [Fact]
    public async Task 本番戦略の判定結果もリスク管理へ射影され昇格を止め続ける()
    {
        var records = ReplayRecords();

        using var publisher = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IHistoricalBarSource>(new FixedBarSource(ReplayBars(31)));
                opts.Services.AddSingleton<IStage0DecisionRecordSource>(new FixedRecordSource(records));
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
                LookbackDays = 30,
                LlmTrainingCutoff = "2026-03-31",
                Strategy = Stage0EvaluationOptions.RecordedReplayStrategyName,
                Symbols = [new Stage0EvaluationOptions.SymbolEntry { Symbol = "AAPL", Market = Market.UnitedStates }],
            }),
            new FixedTimeProvider(Now),
            NullLogger<Stage0EvaluationService>.Instance);

        var publishSession = await publisher.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => driver.RunOnceAsync(CancellationToken.None));
        var verdict = publishSession.Sent.MessagesOf<BacktestEvaluated>().Should().ContainSingle().Which;
        await publisher.StopAsync();

        // **本物の判定器まで進んだ証拠**: 駆動側の事前条件は 1 つも載らず、7 条件の側で落ちている。
        verdict.FailedChecks.Should().NotContain(nameof(Stage0GateCheck.PlaceholderStrategy));
        verdict.FailedChecks.Should().NotContain(nameof(Stage0GateCheck.NoHistoricalBars));
        verdict.FailedChecks.Should().NotContain(nameof(Stage0GateCheck.NoDecisionRecords));
        verdict.FailedChecks.Should().NotContain(nameof(Stage0GateCheck.RecordingMismatch));
        // 🔴 ADR-0039 決定1・決定2: PBO は評価不能・試行数の下限は適用外。残る条件で落ちている。
        verdict.PboEvaluated.Should().BeFalse();
        verdict.PboNotEvaluableReason.Should().Be(nameof(PboNotEvaluableReason.NoSearchSingleTrial));
        verdict.FailedChecks.Should().NotContain(nameof(Stage0GateCheck.TrialCount));
        verdict.FailedChecks.Should().NotContain(nameof(Stage0GateCheck.Overfitting));
        verdict.FailedChecks.Should().Contain(nameof(Stage0GateCheck.DeflatedSharpe));
        // 走らせた戦略の識別子を名乗る（記録の内容から導出された値。IADR-0281 決定3 の「戦略の変更」の鍵）。
        verdict.StrategyId.Should().Be(records.StrategyId);

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

        using var consumer = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IStagePerformanceStore>(performance);
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<BacktestEvaluatedProjectionHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

        await consumer.TrackActivityForTest().InvokeMessageAndWaitAsync(verdict);

        // 射影されている（本番戦略の識別子が段階別実績まで届く）。
        var current = performance.GetCurrent();
        current.BacktestStrategyId.Should().Be(records.StrategyId);

        // 🔴 **否定形（最重要）**: 本物の判定器に到達しても不合格である以上、昇格は止まったままである。
        current.BacktestPassed.Should().BeFalse();
        var after = stageGate.RequestTransition(TradingStage.Stage1Simulate, approver: "owner");
        after.Accepted.Should().BeFalse();
        after.RejectionReasons.Should().Contain(StageGateCriterion.BacktestNotPassed);
        ledger.Load().CurrentStage.Should().Be(TradingStage.Stage0Verification);
        // 空売りを含まない記録なので、実弾の空売り解禁へも効かない（観測は false）。
        current.BacktestIncludesShortSelling.Should().BeFalse();

        await consumer.StopAsync();
    }

    // ---- 本番戦略の通しに使う入力（Stage0EvaluationServiceTests と同じ形を最小限で持つ）----

    private static readonly DateOnly ReplayFrom = new(2026, 8, 10);
    private static readonly DateOnly ReplayTo = new(2026, 9, 9);

    private static IReadOnlyList<PriceBar> ReplayBars(int days) =>
        [.. Enumerable.Range(0, days).Select(i =>
        {
            var close = 100m + (i % 5) - 2m;
            return new PriceBar("AAPL", Market.UnitedStates, ReplayFrom.AddDays(i),
                close, close + 1m, close - 1m, close, 1_000);
        })];

    private static Stage0DecisionRecordSet ReplayRecords()
    {
        Stage0DecisionRecord record = new(
            "AAPL", Market.UnitedStates, ReplayFrom.AddDays(1), "fp", "claude-sonnet-5", VoteCount: 3,
            RawDecisions: [new Stage0RawDecision(1, Stage0DecisionAction.Buy, "根拠", 100m, 2m, 100, 20, false)],
            MajorityAction: Stage0DecisionAction.Buy, MajorityRationale: "根拠", SignedQuantity: 10,
            CostJpy: 1m, InputTokens: 300, OutputTokens: 60);

        return new Stage0DecisionRecordSet(
            ReplayFrom, ReplayTo, [new Stage0RecordedSymbol("AAPL", Market.UnitedStates)],
            new DateOnly(2026, 3, 31), Now, "claude-sonnet-5",
            "ai-decision-replay/claude-sonnet-5/abc123", [record]);
    }

    private sealed class FixedBarSource(IReadOnlyList<PriceBar> bars) : IHistoricalBarSource
    {
        public Task<HistoricalBarLoad> LoadBarsAsync(
            IReadOnlyList<(string Symbol, Market Market)> symbols,
            DateOnly from,
            DateOnly to,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoricalBarLoad(bars, []));
    }

    private sealed class FixedRecordSource(Stage0DecisionRecordSet recordSet) : IStage0DecisionRecordSource
    {
        public Task<Stage0DecisionRecordSet?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Stage0DecisionRecordSet?>(recordSet);
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
