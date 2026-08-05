using AiStockTrading.Audit.Application.Adapters;
using AiStockTrading.Audit.Application.Ports;
using AiStockTrading.Audit.Infrastructure.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace AiStockTrading.Audit.Infrastructure.Tests;

// FR-11, UC-07, IADR-0019: 全ドメインイベントを購読して監査台帳へ記録するハンドラを
// Wolverine のテストハーネス（Wolverine.Tracking）+ インメモリ台帳で検証する。
//
// ADR-0013, IADR-0129, #354: AddMassTransitTestHarness + harness.Consumed から
// TrackActivity + session.Executed へ移行した。表明の意味は同じ（イベントを流し、ハンドラが実行され、
// 台帳に期待した相関・種別で記録される）。購読の列挙（16 件の AddConsumer）はアセンブリ走査に置き換わり、
// **テスト側の発見範囲が本番と同一**になった（列挙のズレという事故の種が消えた）。
public class AuditEventConsumersTests
{
    private static OrderIntent Intent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 1_000m);

    private static Task<IHost> BuildHostAsync(InMemoryAuditEventStore store) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton<IAuditEventStore>(store);
                // 本番（Program.cs）と同じ発見範囲。個別の購読列挙は不要になった。
                opts.Discovery.IncludeAssembly(typeof(PriceMovementDetectedAuditHandler).Assembly);
                // 実ブローカへ接続しない（ローカル・CI ともに RabbitMQ を要求しない）。
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    [Fact]
    public async Task 注文チェーンのイベントは同一_DecisionId_相関で記録される()
    {
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var decisionId = Guid.NewGuid();
        var session0 = await host.TrackActivity().InvokeMessageAndWaitAsync(new TradeDecisionMade(decisionId, Intent(), "買い", DateTimeOffset.UtcNow));
        var session1 = await host.TrackActivity().InvokeMessageAndWaitAsync(new OrderApproved(decisionId, Intent(), 10, DateTimeOffset.UtcNow));
        var session2 = await host.TrackActivity().InvokeMessageAndWaitAsync(new OrderExecuted(decisionId, "ORD-1", OrderStatus.Filled, 10, 1_050m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate));
        session0.Executed.MessagesOf<TradeDecisionMade>().Should().NotBeEmpty();
        session1.Executed.MessagesOf<OrderApproved>().Should().NotBeEmpty();
        session2.Executed.MessagesOf<OrderExecuted>().Should().NotBeEmpty();

        var trail = store.GetByCorrelation(decisionId);
        trail.Select(e => e.EventType).Should()
            .Contain(new[] { "TradeDecisionMade", "OrderApproved", "OrderExecuted" });

        await host.StopAsync();
    }

    [Fact]
    public async Task 訂正取消も同一_DecisionId_相関で注文チェーンに記録される()
    {
        // #154, IADR-0067: 注文履歴テレメトリ。訂正・取消も既存の注文チェーンと同じ相関に載る（FR-11）。
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var decisionId = Guid.NewGuid();
        var session0 = await host.TrackActivity().InvokeMessageAndWaitAsync(new OrderApproved(decisionId, Intent(), 10, DateTimeOffset.UtcNow));
        var session1 = await host.TrackActivity().InvokeMessageAndWaitAsync(
            new OrderModified(decisionId, "ORD-1", 10, 1_000m, 4, 990m, "数量縮小", DateTimeOffset.UtcNow));
        var session2 = await host.TrackActivity().InvokeMessageAndWaitAsync(
            new OrderCancelled(decisionId, "ORD-1", "pause による強制取消", DateTimeOffset.UtcNow));
        session0.Executed.MessagesOf<OrderApproved>().Should().NotBeEmpty();
        session1.Executed.MessagesOf<OrderModified>().Should().NotBeEmpty();
        session2.Executed.MessagesOf<OrderCancelled>().Should().NotBeEmpty();

        var trail = store.GetByCorrelation(decisionId);
        trail.Select(e => e.EventType).Should()
            .Contain(new[] { "OrderApproved", "OrderModified", "OrderCancelled" });

        await host.StopAsync();
    }

    [Fact]
    public async Task 拒否イベントは理由つきで記録される()
    {
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var decisionId = Guid.NewGuid();
        var reasons = new[] { RejectionReason.KillSwitchActive };
        var session0 = await host.TrackActivity().InvokeMessageAndWaitAsync(new OrderRejected(decisionId, Intent(), reasons, DateTimeOffset.UtcNow));
        session0.Executed.MessagesOf<OrderRejected>().Should().NotBeEmpty();

        var trail = store.GetByCorrelation(decisionId);
        trail.Should().ContainSingle(e => e.EventType == "OrderRejected")
            .Which.Summary.Should().Contain(nameof(RejectionReason.KillSwitchActive));

        await host.StopAsync();
    }

    [Fact]
    public async Task 設定変更と報告書確定も監査台帳に記録される()
    {
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var session0 = await host.TrackActivity().InvokeMessageAndWaitAsync(new AssumptionsChanged(2, "owner", "税率見直し", DateTimeOffset.UtcNow));
        var session1 = await host.TrackActivity().InvokeMessageAndWaitAsync(new ReportConfirmed("daily-2026-07-10", "Daily", "owner", 2, DateTimeOffset.UtcNow));
        session0.Executed.MessagesOf<AssumptionsChanged>().Should().NotBeEmpty();
        session1.Executed.MessagesOf<ReportConfirmed>().Should().NotBeEmpty();

        var reportCorr = AiStockTrading.Audit.Application.Services.AuditEntryFactory
            .From(new ReportConfirmed("daily-2026-07-10", "Daily", "x", 0, DateTimeOffset.UtcNow), Guid.NewGuid(), DateTimeOffset.UtcNow)
            .CorrelationId;
        store.GetByCorrelation(reportCorr).Should().ContainSingle(e => e.EventType == "ReportConfirmed");

        await host.StopAsync();
    }

    [Fact]
    public async Task 費用しきい値到達と情報収集完了も監査台帳に記録される()
    {
        // #80, FR-11: これまで未購読だった 2 イベントも「全イベントの時系列記録」に含める。
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var collectId = Guid.NewGuid();
        var session0 = await host.TrackActivity().InvokeMessageAndWaitAsync(new CostThresholdReached("2026-07", "Llm", 1.00m, "Halted", DateTimeOffset.UtcNow));
        var session1 = await host.TrackActivity().InvokeMessageAndWaitAsync(new InformationCollected(collectId, 3, DateTimeOffset.UtcNow));
        session0.Executed.MessagesOf<CostThresholdReached>().Should().NotBeEmpty();
        session1.Executed.MessagesOf<InformationCollected>().Should().NotBeEmpty();

        store.GetByCorrelation(collectId).Should().ContainSingle(e => e.EventType == "InformationCollected");

        var costCorr = AiStockTrading.Audit.Application.Services.AuditEntryFactory
            .From(new CostThresholdReached("2026-07", "Llm", 0m, "x", DateTimeOffset.UtcNow), Guid.NewGuid(), DateTimeOffset.UtcNow)
            .CorrelationId;
        store.GetByCorrelation(costCorr).Should().ContainSingle(e => e.EventType == "CostThresholdReached");

        await host.StopAsync();
    }

    [Fact]
    public async Task 段階遷移も監査台帳に記録される()
    {
        // FR-20, FR-11, #167, IADR-0082: 段階ゲートの遷移も中央監査台帳へ集約する（全イベントの時系列記録）。
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var session0 = await host.TrackActivity().InvokeMessageAndWaitAsync(
            new StageTransitioned(1, 0, 1, "Promotion", "owner", "利用者承認による昇格", DateTimeOffset.UtcNow));
        session0.Executed.MessagesOf<StageTransitioned>().Should().NotBeEmpty();

        var stageCorr = AiStockTrading.Audit.Application.Services.AuditEntryFactory
            .From(new StageTransitioned(0, 0, 0, "Promotion", "x", "y", DateTimeOffset.UtcNow), Guid.NewGuid(), DateTimeOffset.UtcNow)
            .CorrelationId;
        store.GetByCorrelation(stageCorr).Should().ContainSingle(e => e.EventType == "StageTransitioned");

        await host.StopAsync();
    }

    [Fact]
    public async Task 撤退基準到達も段階ゲート相関で監査台帳に記録される()
    {
        // FR-20, FR-11, #166, IADR-0083: 撤退基準到達（自動安全側の発火）も中央監査台帳へ集約する。
        // 段階遷移と同じ "stage-gate" 相関で束ね、撤退と遷移をまとめて辿れる（撤退は StageTransitioned を伴わない）。
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var session0 = await host.TrackActivity().InvokeMessageAndWaitAsync(new WithdrawalTriggered(
            0, "DrawdownBreachedMultiple", HaltNewEntries: true, DateTimeOffset.UtcNow));
        session0.Executed.MessagesOf<WithdrawalTriggered>().Should().NotBeEmpty();

        var stageCorr = AiStockTrading.Audit.Application.Services.AuditEntryFactory
            .From(new StageTransitioned(0, 0, 0, "Promotion", "x", "y", DateTimeOffset.UtcNow), Guid.NewGuid(), DateTimeOffset.UtcNow)
            .CorrelationId;
        store.GetByCorrelation(stageCorr).Should().ContainSingle(e => e.EventType == "WithdrawalTriggered");

        await host.StopAsync();
    }

    [Fact]
    public async Task バックテストverdictも段階ゲート相関で監査台帳に記録される()
    {
        // FR-20, FR-15, FR-11, #164, IADR-0089: バックテスト verdict（Stage 0 合格判定・Stage 0→1 解錠）も
        // 中央監査台帳へ集約する。段階遷移・撤退と同じ "stage-gate" 相関で束ね、段階ゲート系をまとめて辿れる。
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var session0 = await host.TrackActivity().InvokeMessageAndWaitAsync(new BacktestEvaluated(
            Passed: true, MaxDrawdownRatio: 0.08m, DeflatedSharpe: 1.2,
            ProbabilityOfBacktestOverfitting: 0.1, FailedChecks: string.Empty, DateTimeOffset.UtcNow));
        session0.Executed.MessagesOf<BacktestEvaluated>().Should().NotBeEmpty();

        var stageCorr = AiStockTrading.Audit.Application.Services.AuditEntryFactory
            .From(new StageTransitioned(0, 0, 0, "Promotion", "x", "y", DateTimeOffset.UtcNow), Guid.NewGuid(), DateTimeOffset.UtcNow)
            .CorrelationId;
        store.GetByCorrelation(stageCorr).Should().ContainSingle(e => e.EventType == "BacktestEvaluated");

        await host.StopAsync();
    }

    [Fact]
    public async Task 市場イベントは_EventId_相関で記録される()
    {
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var eventId = Guid.NewGuid();
        var session0 = await host.TrackActivity().InvokeMessageAndWaitAsync(new StopLossTriggered(eventId, "7203", Market.Japan, TradeSide.Buy, 5, 950m, 940m, DateTimeOffset.UtcNow));
        session0.Executed.MessagesOf<StopLossTriggered>().Should().NotBeEmpty();

        store.GetByCorrelation(eventId).Should().ContainSingle(e => e.EventType == "StopLossTriggered");

        await host.StopAsync();
    }
}
