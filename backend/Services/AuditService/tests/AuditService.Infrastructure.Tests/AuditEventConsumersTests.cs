using AiStockTrading.Audit.Application.Adapters;
using AiStockTrading.Audit.Application.Ports;
using AiStockTrading.Audit.Infrastructure.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
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
        var session0 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new TradeDecisionMade(decisionId, Intent(), "買い", DateTimeOffset.UtcNow));
        var session1 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderApproved(decisionId, Intent(), 10, DateTimeOffset.UtcNow));
        var session2 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderExecuted(decisionId, "ORD-1", OrderStatus.Filled, 10, 1_050m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate));
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
        var session0 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderApproved(decisionId, Intent(), 10, DateTimeOffset.UtcNow));
        var session1 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new OrderModified(decisionId, "ORD-1", 10, 1_000m, 4, 990m, "数量縮小", DateTimeOffset.UtcNow));
        var session2 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
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
        var session0 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderRejected(decisionId, Intent(), reasons, DateTimeOffset.UtcNow));
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

        var session0 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new AssumptionsChanged(2, "owner", "税率見直し", DateTimeOffset.UtcNow));
        var session1 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new ReportConfirmed("daily-2026-07-10", "Daily", "owner", 2, DateTimeOffset.UtcNow));
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
        var session0 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new CostThresholdReached("2026-07", "Llm", 1.00m, "Halted", DateTimeOffset.UtcNow));
        var session1 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new InformationCollected(collectId, 3, DateTimeOffset.UtcNow));
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

        var session0 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new StageTransitioned(1, 0, 1, "Promotion", "owner", "利用者承認による昇格", DateTimeOffset.UtcNow, 100, false));
        session0.Executed.MessagesOf<StageTransitioned>().Should().NotBeEmpty();

        var stageCorr = AiStockTrading.Audit.Application.Services.AuditEntryFactory
            .From(new StageTransitioned(0, 0, 0, "Promotion", "x", "y", DateTimeOffset.UtcNow, 100, false), Guid.NewGuid(), DateTimeOffset.UtcNow)
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

        var session0 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new WithdrawalTriggered(
            0, "DrawdownBreachedMultiple", HaltNewEntries: true, DateTimeOffset.UtcNow));
        session0.Executed.MessagesOf<WithdrawalTriggered>().Should().NotBeEmpty();

        var stageCorr = AiStockTrading.Audit.Application.Services.AuditEntryFactory
            .From(new StageTransitioned(0, 0, 0, "Promotion", "x", "y", DateTimeOffset.UtcNow, 100, false), Guid.NewGuid(), DateTimeOffset.UtcNow)
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

        var session0 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new BacktestEvaluated(
            Passed: true, MaxDrawdownRatio: 0.08m, DeflatedSharpe: 1.2,
            ProbabilityOfBacktestOverfitting: 0.1, FailedChecks: string.Empty, DateTimeOffset.UtcNow));
        session0.Executed.MessagesOf<BacktestEvaluated>().Should().NotBeEmpty();

        var stageCorr = AiStockTrading.Audit.Application.Services.AuditEntryFactory
            .From(new StageTransitioned(0, 0, 0, "Promotion", "x", "y", DateTimeOffset.UtcNow, 100, false), Guid.NewGuid(), DateTimeOffset.UtcNow)
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
        var session0 = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new StopLossTriggered(eventId, "7203", Market.Japan, TradeSide.Buy, 5, 950m, 940m, DateTimeOffset.UtcNow));
        session0.Executed.MessagesOf<StopLossTriggered>().Should().NotBeEmpty();

        store.GetByCorrelation(eventId).Should().ContainSingle(e => e.EventType == "StopLossTriggered");

        await host.StopAsync();
    }

    // FR-10, FR-11, #381 停止側, IADR-0198 決定3: 鮮度切れのレートでの決済を台帳へ記録する。
    //
    // 🔴 **本経路が「いつのレートで決済したか」を 7 年保持へ入れる唯一の手段である。**
    // 台帳の行（`ApprovedOrderRow`）は観測日の列を持たないが、**イベント全量は JSON で保存される**——
    // ここが通らなければ観測日はどこにも残らない。
    [Fact]
    public async Task 鮮度切れでの決済は_銘柄ごとの相関で台帳へ記録される()
    {
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var now = DateTimeOffset.UtcNow;
        var evt = new PositionClosedWithStaleFxRate(
            "7203", Market.Japan, "JPY", 300, 0.0067m, now.AddDays(-31), 31, now);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(evt);
        session.Executed.MessagesOf<PositionClosedWithStaleFxRate>().Should().NotBeEmpty();

        var correlation = AiStockTrading.Audit.Application.Services.AuditEntryFactory
            .From(evt, Guid.NewGuid(), now).CorrelationId;

        var entry = store.GetByCorrelation(correlation)
            .Should().ContainSingle(e => e.EventType == nameof(PositionClosedWithStaleFxRate)).Subject;
        // 🔴 観測日が残っていなければ本イベントを足した意味が無い。
        entry.Summary.Should().Contain("観測日");

        await host.StopAsync();
    }

    // 🔴 **否定形: 抑止しない。** 状態（`FxRateStale`）は 1 日 1 回へ抑止するが、
    // **取引は 1 件ずつ残さなければ後から件数も金額も復元できない**（IADR-0198 決定3）。
    [Fact]
    public async Task 鮮度切れでの決済は_同じ銘柄でも件数ぶん台帳へ残る()
    {
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var now = DateTimeOffset.UtcNow;
        var first = new PositionClosedWithStaleFxRate(
            "7203", Market.Japan, "JPY", 300, 0.0067m, now.AddDays(-31), 31, now);
        var second = first with { Quantity = 100, OccurredAt = now.AddMinutes(5) };

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(first);
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(second);

        var correlation = AiStockTrading.Audit.Application.Services.AuditEntryFactory
            .From(first, Guid.NewGuid(), now).CorrelationId;

        store.GetByCorrelation(correlation).Should().HaveCount(2, "取引は 1 件ずつ残す（抑止しない）");

        await host.StopAsync();
    }

    // FR-05, FR-10, FR-11, #331, IADR-0210/0211: 損切りのブローカー側逆指値への一本化で足した 3 イベント。
    // いずれも**注文チェーンと同じ DecisionId 相関**で台帳に載らなければ、後から
    // 「なぜ発注されなかったか」「なぜ建玉が消えたか」を 1 本の相関で辿れない。

    [Fact]
    public async Task 見送りは注文チェーンに拒否とは別のEventTypeで載る()
    {
        // 🔴 見送り（届いていない）を Rejected（証券会社が受理しなかった）と同じ種別で数えると、
        // 「拒否」の集計が接続障害で汚染される（FR-05・IADR-0211）。別 EventType であることを固定する。
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var decisionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderApproved(decisionId, Intent(), 10, now));
        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new OrderDispatchForgone(decisionId, Intent(), OrderDispatchForgoneReason.BrokerUnavailable, now));
        session.Executed.MessagesOf<OrderDispatchForgone>().Should().NotBeEmpty();

        var trail = store.GetByCorrelation(decisionId);
        trail.Select(e => e.EventType).Should().Contain([nameof(OrderApproved), nameof(OrderDispatchForgone)]);
        trail.Select(e => e.EventType).Should().NotContain(nameof(OrderRejected));

        await host.StopAsync();
    }

    [Fact]
    public async Task 保護逆指値の発注はエントリーと同じ相関で載る()
    {
        // 「建玉あり ⇒ 有効な逆指値あり」の証跡はエントリーと 1 本で辿れなければ監査に使えない。
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var entryDecisionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var closeIntent = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 10, 950m, PositionEffect.Close);
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderApproved(entryDecisionId, Intent(), 10, now));
        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new ProtectiveStopPlaced(entryDecisionId, Guid.NewGuid(), "stop-1", closeIntent, 950m, 1, now));
        session.Executed.MessagesOf<ProtectiveStopPlaced>().Should().NotBeEmpty();

        store.GetByCorrelation(entryDecisionId).Select(e => e.EventType)
            .Should().Contain([nameof(OrderApproved), nameof(ProtectiveStopPlaced)]);

        await host.StopAsync();
    }

    [Fact]
    public async Task 保護喪失は建玉が消えた理由として台帳に残る()
    {
        // 利用者の承認なしに注文取消・建玉決済が起きる事象であり、この記録が唯一の一次証跡になる。
        // Remediation=None（解消も失敗）は要約から人手対応が要ると読めなければならない。
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var entryDecisionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new ProtectiveStopCoverageLost(entryDecisionId, "AAPL", Market.UnitedStates,
                ProtectiveStopLossCause.RejectedAtEntry, ProtectiveStopRemediation.None, 10,
                CloseDecisionId: null, CloseIntent: null, now));
        session.Executed.MessagesOf<ProtectiveStopCoverageLost>().Should().NotBeEmpty();

        var entry = store.GetByCorrelation(entryDecisionId)
            .Should().ContainSingle(e => e.EventType == nameof(ProtectiveStopCoverageLost)).Subject;
        entry.CorrelationId.Should().Be(entryDecisionId, "エントリーの DecisionId で注文チェーンへ束ねる");
        entry.Symbol.Should().Be("AAPL");

        await host.StopAsync();
    }
}
