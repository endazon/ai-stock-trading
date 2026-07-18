using AiStockTrading.Audit.Application.Adapters;
using AiStockTrading.Audit.Application.Ports;
using AiStockTrading.Audit.Worker.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.Audit.Worker.Tests;

// FR-11, UC-07, IADR-0019: 全ドメインイベントを購読して監査台帳へ記録する Consumer を
// MassTransit テストハーネス + インメモリ台帳で検証する。
public class AuditEventConsumersTests
{
    private static OrderIntent Intent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, 10, 1_000m);

    private static ServiceProvider BuildProvider(InMemoryAuditEventStore store) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock, SystemClock>()
            .AddSingleton<IAuditEventStore>(store)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<PriceMovementDetectedAuditConsumer>();
                x.AddConsumer<StopLossTriggeredAuditConsumer>();
                x.AddConsumer<TradeDecisionMadeAuditConsumer>();
                x.AddConsumer<OrderApprovedAuditConsumer>();
                x.AddConsumer<OrderRejectedAuditConsumer>();
                x.AddConsumer<OrderExecutedAuditConsumer>();
                x.AddConsumer<OrderModifiedAuditConsumer>();
                x.AddConsumer<OrderCancelledAuditConsumer>();
                x.AddConsumer<AssumptionsChangedAuditConsumer>();
                x.AddConsumer<ReportConfirmedAuditConsumer>();
                x.AddConsumer<CostThresholdReachedAuditConsumer>();
                x.AddConsumer<LlmCostIncurredAuditConsumer>();
                x.AddConsumer<InformationCollectedAuditConsumer>();
                x.AddConsumer<StageTransitionedAuditConsumer>();
                x.AddConsumer<WithdrawalTriggeredAuditConsumer>();
                x.AddConsumer<BacktestEvaluatedAuditConsumer>();
            })
            .BuildServiceProvider(true);

    [Fact]
    public async Task 注文チェーンのイベントは同一_DecisionId_相関で記録される()
    {
        var store = new InMemoryAuditEventStore();
        await using var provider = BuildProvider(store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var decisionId = Guid.NewGuid();
        await harness.Bus.Publish(new TradeDecisionMade(decisionId, Intent(), "買い", DateTimeOffset.UtcNow));
        await harness.Bus.Publish(new OrderApproved(decisionId, Intent(), 10, DateTimeOffset.UtcNow));
        await harness.Bus.Publish(new OrderExecuted(decisionId, "ORD-1", OrderStatus.Filled, 10, 1_050m, DateTimeOffset.UtcNow));

        (await harness.Consumed.Any<TradeDecisionMade>()).Should().BeTrue();
        (await harness.Consumed.Any<OrderApproved>()).Should().BeTrue();
        (await harness.Consumed.Any<OrderExecuted>()).Should().BeTrue();

        var trail = store.GetByCorrelation(decisionId);
        trail.Select(e => e.EventType).Should()
            .Contain(new[] { "TradeDecisionMade", "OrderApproved", "OrderExecuted" });

        await harness.Stop();
    }

    [Fact]
    public async Task 訂正取消も同一_DecisionId_相関で注文チェーンに記録される()
    {
        // #154, IADR-0067: 注文履歴テレメトリ。訂正・取消も既存の注文チェーンと同じ相関に載る（FR-11）。
        var store = new InMemoryAuditEventStore();
        await using var provider = BuildProvider(store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var decisionId = Guid.NewGuid();
        await harness.Bus.Publish(new OrderApproved(decisionId, Intent(), 10, DateTimeOffset.UtcNow));
        await harness.Bus.Publish(
            new OrderModified(decisionId, "ORD-1", 10, 1_000m, 4, 990m, "数量縮小", DateTimeOffset.UtcNow));
        await harness.Bus.Publish(
            new OrderCancelled(decisionId, "ORD-1", "pause による強制取消", DateTimeOffset.UtcNow));

        // 3 イベントすべての消費完了を待ってから台帳を読む（並行負荷下で承認の消費が遅れると取りこぼすため）。
        (await harness.Consumed.Any<OrderApproved>()).Should().BeTrue();
        (await harness.Consumed.Any<OrderModified>()).Should().BeTrue();
        (await harness.Consumed.Any<OrderCancelled>()).Should().BeTrue();

        var trail = store.GetByCorrelation(decisionId);
        trail.Select(e => e.EventType).Should()
            .Contain(new[] { "OrderApproved", "OrderModified", "OrderCancelled" });

        await harness.Stop();
    }

    [Fact]
    public async Task 拒否イベントは理由つきで記録される()
    {
        var store = new InMemoryAuditEventStore();
        await using var provider = BuildProvider(store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var decisionId = Guid.NewGuid();
        var reasons = new[] { RejectionReason.KillSwitchActive };
        await harness.Bus.Publish(new OrderRejected(decisionId, Intent(), reasons, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<OrderRejected>()).Should().BeTrue();

        var trail = store.GetByCorrelation(decisionId);
        trail.Should().ContainSingle(e => e.EventType == "OrderRejected")
            .Which.Summary.Should().Contain(nameof(RejectionReason.KillSwitchActive));

        await harness.Stop();
    }

    [Fact]
    public async Task 設定変更と報告書確定も監査台帳に記録される()
    {
        var store = new InMemoryAuditEventStore();
        await using var provider = BuildProvider(store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new AssumptionsChanged(2, "owner", "税率見直し", DateTimeOffset.UtcNow));
        await harness.Bus.Publish(new ReportConfirmed("daily-2026-07-10", "Daily", "owner", 2, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<AssumptionsChanged>()).Should().BeTrue();
        (await harness.Consumed.Any<ReportConfirmed>()).Should().BeTrue();

        var reportCorr = AiStockTrading.Audit.Application.Services.AuditEntryFactory
            .From(new ReportConfirmed("daily-2026-07-10", "Daily", "x", 0, DateTimeOffset.UtcNow), Guid.NewGuid(), DateTimeOffset.UtcNow)
            .CorrelationId;
        store.GetByCorrelation(reportCorr).Should().ContainSingle(e => e.EventType == "ReportConfirmed");

        await harness.Stop();
    }

    [Fact]
    public async Task 費用しきい値到達と情報収集完了も監査台帳に記録される()
    {
        // #80, FR-11: これまで未購読だった 2 イベントも「全イベントの時系列記録」に含める。
        var store = new InMemoryAuditEventStore();
        await using var provider = BuildProvider(store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var collectId = Guid.NewGuid();
        await harness.Bus.Publish(new CostThresholdReached("2026-07", "Llm", 1.00m, "Halted", DateTimeOffset.UtcNow));
        await harness.Bus.Publish(new InformationCollected(collectId, 3, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<CostThresholdReached>()).Should().BeTrue();
        (await harness.Consumed.Any<InformationCollected>()).Should().BeTrue();

        store.GetByCorrelation(collectId).Should().ContainSingle(e => e.EventType == "InformationCollected");

        var costCorr = AiStockTrading.Audit.Application.Services.AuditEntryFactory
            .From(new CostThresholdReached("2026-07", "Llm", 0m, "x", DateTimeOffset.UtcNow), Guid.NewGuid(), DateTimeOffset.UtcNow)
            .CorrelationId;
        store.GetByCorrelation(costCorr).Should().ContainSingle(e => e.EventType == "CostThresholdReached");

        await harness.Stop();
    }

    [Fact]
    public async Task 段階遷移も監査台帳に記録される()
    {
        // FR-20, FR-11, #167, IADR-0082: 段階ゲートの遷移も中央監査台帳へ集約する（全イベントの時系列記録）。
        var store = new InMemoryAuditEventStore();
        await using var provider = BuildProvider(store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(
            new StageTransitioned(1, 0, 1, "Promotion", "owner", "利用者承認による昇格", DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<StageTransitioned>()).Should().BeTrue();

        var stageCorr = AiStockTrading.Audit.Application.Services.AuditEntryFactory
            .From(new StageTransitioned(0, 0, 0, "Promotion", "x", "y", DateTimeOffset.UtcNow), Guid.NewGuid(), DateTimeOffset.UtcNow)
            .CorrelationId;
        store.GetByCorrelation(stageCorr).Should().ContainSingle(e => e.EventType == "StageTransitioned");

        await harness.Stop();
    }

    [Fact]
    public async Task 撤退基準到達も段階ゲート相関で監査台帳に記録される()
    {
        // FR-20, FR-11, #166, IADR-0083: 撤退基準到達（自動安全側の発火）も中央監査台帳へ集約する。
        // 段階遷移と同じ "stage-gate" 相関で束ね、撤退と遷移をまとめて辿れる（撤退は StageTransitioned を伴わない）。
        var store = new InMemoryAuditEventStore();
        await using var provider = BuildProvider(store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new WithdrawalTriggered(
            0, "DrawdownBreachedMultiple", HaltNewEntries: true, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<WithdrawalTriggered>()).Should().BeTrue();

        var stageCorr = AiStockTrading.Audit.Application.Services.AuditEntryFactory
            .From(new StageTransitioned(0, 0, 0, "Promotion", "x", "y", DateTimeOffset.UtcNow), Guid.NewGuid(), DateTimeOffset.UtcNow)
            .CorrelationId;
        store.GetByCorrelation(stageCorr).Should().ContainSingle(e => e.EventType == "WithdrawalTriggered");

        await harness.Stop();
    }

    [Fact]
    public async Task バックテストverdictも段階ゲート相関で監査台帳に記録される()
    {
        // FR-20, FR-15, FR-11, #164, IADR-0089: バックテスト verdict（Stage 0 合格判定・Stage 0→1 解錠）も
        // 中央監査台帳へ集約する。段階遷移・撤退と同じ "stage-gate" 相関で束ね、段階ゲート系をまとめて辿れる。
        var store = new InMemoryAuditEventStore();
        await using var provider = BuildProvider(store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new BacktestEvaluated(
            Passed: true, MaxDrawdownRatio: 0.08m, DeflatedSharpe: 1.2,
            ProbabilityOfBacktestOverfitting: 0.1, FailedChecks: string.Empty, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<BacktestEvaluated>()).Should().BeTrue();

        var stageCorr = AiStockTrading.Audit.Application.Services.AuditEntryFactory
            .From(new StageTransitioned(0, 0, 0, "Promotion", "x", "y", DateTimeOffset.UtcNow), Guid.NewGuid(), DateTimeOffset.UtcNow)
            .CorrelationId;
        store.GetByCorrelation(stageCorr).Should().ContainSingle(e => e.EventType == "BacktestEvaluated");

        await harness.Stop();
    }

    [Fact]
    public async Task 市場イベントは_EventId_相関で記録される()
    {
        var store = new InMemoryAuditEventStore();
        await using var provider = BuildProvider(store);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var eventId = Guid.NewGuid();
        await harness.Bus.Publish(new StopLossTriggered(eventId, "7203", Market.Japan, TradeSide.Buy, 5, 950m, 940m, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<StopLossTriggered>()).Should().BeTrue();

        store.GetByCorrelation(eventId).Should().ContainSingle(e => e.EventType == "StopLossTriggered");

        await harness.Stop();
    }
}
