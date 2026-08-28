using System.Reflection;
using AiStockTrading.Audit.Application.Adapters;
using AiStockTrading.Audit.Application.Ports;
using AiStockTrading.Audit.Application.Services;
using AiStockTrading.Audit.Application.State;
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

// FR-11, UC-07, ADR-0016 決定15, #339, IADR-0226:
// **取引サイクル 1 周で必須イベントがすべて記録される**ことの完全性テスト。
//
// 「必須イベント」は記憶で挙げず、計画（04_workflows/01_scheduled-trading-cycle）のフロー図・
// シーケンス図から機械的に割り当てた（作業仕様書 §2.1 に全 33 種の割り当て表がある）。
// 定時サイクル 1 周で必ず通る集合は次の 2 経路だけである。
//   承認経路: InformationCollected → TradeDecisionMade → OrderApproved → OrderExecuted
//   拒否経路: InformationCollected → TradeDecisionMade → OrderRejected
//
// `AuditConsumerCoverageTests` は「全イベントにハンドラがある」ことを見るが、
// **1 周を通したときに実際に台帳へ落ちること**は見ていない。ここがその穴を塞ぐ。
public class AuditCycleCompletenessTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 28, 13, 30, 0, TimeSpan.Zero);

    private static OrderIntent Intent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 1_000m);

    private static Task<IHost> BuildHostAsync(InMemoryAuditEventStore store) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton<IAuditEventStore>(store);
                // 本番（Program.cs）と同じ発見範囲。
                opts.Discovery.IncludeAssembly(typeof(PriceMovementDetectedAuditHandler).Assembly);
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    [Fact]
    public async Task 取引サイクル_1_周_承認経路_の必須イベントがすべて台帳へ残る()
    {
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var decisionId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new InformationCollected(collectionId, 5, T0));
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new TradeDecisionMade(decisionId, Intent(), "上昇トレンドのため買い", T0.AddSeconds(30)));
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new OrderApproved(decisionId, Intent(), 10, T0.AddSeconds(60)));
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new OrderExecuted(decisionId, "ORD-1", OrderStatus.Filled, 10, 1_005m, T0.AddSeconds(90),
                BrokerProvider.MoomooSimulate));

        // 収集は自らの EventId 相関、判断以降は DecisionId 相関である（2 本の相関で 1 周を覆う）。
        store.GetByCorrelation(collectionId).Select(e => e.EventType)
            .Should().Contain(nameof(InformationCollected));

        var chain = store.GetByCorrelation(decisionId);
        chain.Select(e => e.EventType).Should().Equal(
            nameof(TradeDecisionMade), nameof(OrderApproved), nameof(OrderExecuted));

        // 🔴 時系列であること。監査は「いつ・何を根拠に・何をしたか」を辿るものであり、
        // 順序が崩れると根拠と結果の前後関係が読めない。
        chain.Select(e => e.OccurredAt).Should().BeInAscendingOrder();

        await host.StopAsync();
    }

    [Fact]
    public async Task 取引サイクル_1_周_拒否経路_の必須イベントがすべて台帳へ残る()
    {
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var decisionId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new InformationCollected(collectionId, 5, T0));
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new TradeDecisionMade(decisionId, Intent(), "上昇トレンドのため買い", T0.AddSeconds(30)));
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new OrderRejected(decisionId, Intent(), [RejectionReason.PerOrderAmountExceeded], T0.AddSeconds(60)));

        store.GetByCorrelation(collectionId).Select(e => e.EventType)
            .Should().Contain(nameof(InformationCollected));

        var chain = store.GetByCorrelation(decisionId);
        chain.Select(e => e.EventType).Should().Equal(
            nameof(TradeDecisionMade), nameof(OrderRejected));

        // 拒否は理由まで残る（FR-11「何を根拠に」）。
        chain.Last().Summary.Should().Contain(nameof(RejectionReason.PerOrderAmountExceeded));

        await host.StopAsync();
    }

    // 経費計上は取引サイクルとは独立の事象だが、**建玉ごとの相関で 1 本に辿れる**ことは
    // 「建玉単位で紐づけられる」（ADR-0016 決定15）の実体である。
    [Fact]
    public async Task 経費計上は建玉ごとの相関で台帳へ残り_別建玉と混ざらない()
    {
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var day = new DateOnly(2026, 8, 28);
        var apple = new TradeExpenseRecorded(new TradeExpense(
            "AAPL", Market.UnitedStates, TradeExpenseCategory.Commission, 1.00m, day, "ORD-1", T0));
        var appleBorrow = new TradeExpenseRecorded(new TradeExpense(
            "AAPL", Market.UnitedStates, TradeExpenseCategory.BorrowFee, 0.50m, day, "ACC-1", T0.AddMinutes(1)));
        var microsoft = new TradeExpenseRecorded(new TradeExpense(
            "MSFT", Market.UnitedStates, TradeExpenseCategory.Commission, 2.00m, day, "ORD-2", T0.AddMinutes(2)));

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(apple);
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(appleBorrow);
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(microsoft);

        var appleCorrelation = AuditEntryFactory.From(apple, Guid.NewGuid(), T0).CorrelationId;
        var msftCorrelation = AuditEntryFactory.From(microsoft, Guid.NewGuid(), T0).CorrelationId;

        var appleTrail = store.GetByCorrelation(appleCorrelation);
        appleTrail.Should().HaveCount(2, "同じ建玉の経費は区分が違っても 1 本の相関で辿れる");
        appleTrail.Should().AllSatisfy(e => e.Symbol.Should().Be("AAPL"));

        store.GetByCorrelation(msftCorrelation).Should().ContainSingle()
            .Which.Symbol.Should().Be("MSFT");

        await host.StopAsync();
    }

    // 🔴 **写像の全数。** `AuditConsumerCoverageTests` はハンドラの存在（＝Wolverine が扱えること）を見るが、
    // 写像（`AuditEntryFactory.From`）が全イベントぶんそろっていることは誰も見ていなかった。
    // ハンドラは写像を呼ぶため実際にはコンパイルで守られているが、**その依存関係は暗黙である** ——
    // 明示的に固定しておくと、写像だけを消す変更がここで落ちる。
    [Fact]
    public void 監査写像は契約イベントの全数をカバーする()
    {
        var mapped = typeof(AuditEntryFactory)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == nameof(AuditEntryFactory.From) && m.ReturnType == typeof(AuditEntry))
            .Select(m => m.GetParameters()[0].ParameterType)
            .ToList();

        var discovered = EventTypeDiscovery.GetEventTypes();

        mapped.Should().BeEquivalentTo(discovered,
            "新イベントを追加したら AuditEntryFactory の写像も追加すること（写像漏れ）。"
            + "イベントを削除したら写像も外すこと（写像残り）。"
            + $"写像={mapped.Count} 件 / 母集合={discovered.Count} 件");
    }

    // 🔴 **全イベントが実際に台帳へ落ちることの実走テスト。**
    //
    // `AuditConsumerCoverageTests` は「Wolverine がその型を扱える」ことを実行時の発見結果で見るが、
    // **ハンドラが台帳へ 1 行書くこと**までは見ていない（写像が例外を投げる・別の型で記録する等は素通りする）。
    // ここで契約イベントの**全数**を 1 件ずつ流し、種別ごとに記録が残ることを確かめる。
    //
    // 標本は**記憶で列挙しない** —— 下の `Samples()` が返す型集合が
    // `EventTypeDiscovery.GetEventTypes()` と完全一致することを先に表明し、
    // **足し忘れ（新イベントの標本が無い）も外し忘れ（消えた型の標本が残る）も赤くする。**
    [Fact]
    public void 標本は契約イベントの全数と完全に一致する()
    {
        Samples().Select(s => s.GetType()).Should().BeEquivalentTo(
            EventTypeDiscovery.GetEventTypes(),
            "新イベントを追加したら本テストの標本も追加すること（標本漏れ）。"
            + "イベントを削除したら標本も外すこと（標本残り）");
    }

    [Fact]
    public async Task 契約イベントの全数が監査台帳へ記録される()
    {
        var store = new InMemoryAuditEventStore();
        using var host = await BuildHostAsync(store);

        var samples = Samples();
        foreach (var sample in samples)
        {
            await host.TrackActivityForTest().InvokeMessageAndWaitAsync(sample);
        }

        var recorded = store.GetRecent(samples.Count * 2).Select(e => e.EventType).ToHashSet(StringComparer.Ordinal);
        var missing = samples.Select(s => s.GetType().Name).Where(n => !recorded.Contains(n)).ToList();

        missing.Should().BeEmpty(
            "全ドメインイベントは監査台帳へ記録する（FR-11「いつ・何を根拠に・何をしたか」）。"
            + "\n記録されなかった種別: " + string.Join(", ", missing));

        // 種別が 1 件ずつ、取り違えなく記録されている（別の種別名で書かれていないこと）。
        recorded.Should().HaveCount(samples.Count);

        await host.StopAsync();
    }

    // 契約イベントの標本（全数）。値は写像を通すためのものであり、統制の判定には用いない。
    private static IReadOnlyList<object> Samples()
    {
        var t = T0;
        var day = new DateOnly(2026, 8, 28);
        var decisionId = Guid.NewGuid();

        return
        [
            new AssumptionsChanged(3, "endazon", "前提の見直し", t),
            new BacktestEvaluated(true, 0.08m, 1.4d, 0.2d, string.Empty, t),
            new BorrowFeeAccrualUnavailable("AAPL", Market.UnitedStates, day, "料率を取得できない", t),
            new BorrowFeeAccrued("AAPL", Market.UnitedStates, day, 0.05m, 1_000m, 0.14m, t),
            new BrokerAccountObserved(
                BrokerProvider.MoomooSimulate, new BrokerAccountState(AccountType.Margin, 1_000m), t),
            new BrokerAvailabilityObserved(BrokerProvider.MoomooSimulate, t, TimeSpan.FromMinutes(30)),
            new BrokerPositionsObserved(
                [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 10, 1_000m)], t),
            new BuyInInferred(
                Guid.NewGuid(), "AAPL", Market.UnitedStates, 10, 5, 0, 5, 5,
                [new BuyInCoveringFill(TradeSide.Buy, 5, 1_010m, t)], day, t, t),
            new CostThresholdReached("2026-08", "llm", 0.8m, "Warning", t),
            new DailyPolicyUnconfirmed(day, t),
            new FxRateSourceFellBack("JPY", "fallback-source", 2, 3, t),
            new FxRateSourcePrimaryRestored("JPY", "primary-source", t.AddHours(-3), t),
            new FxRateSourceUsed("JPY", "primary-source", 1, 3, t),
            new FxRateStale("JPY", t.AddDays(-10), 10d, 7d, 30d, t),
            new GeneralWebCollectionStateChanged("news", true, "必須情報源が全滅", t.AddDays(30), t),
            new GoodFaithViolationRecorded(
                Guid.NewGuid(), decisionId, "ORD-1", "AAPL", Market.UnitedStates, 1_000m, 500m, day, t, t),
            new GoodFaithViolationsCleared("endazon", "入金により解消", ["ORD-1"], 0, t),
            new InformationCollected(Guid.NewGuid(), 5, t),
            new InformationSourceDegraded("news", "LimitedDegradation", ["finnhub-company-news"], true, t),
            new InformationSourceRecovered("news", t.AddHours(-2), 4, t),
            new LlmCostIncurred(12.5m, t),
            new LlmFallbackFired("report-monthly", "claude-opus-5", "claude-sonnet-5", "FallbackFired", t),
            new MaintenanceMarginReductionExecuted(
                Guid.NewGuid(), 0.38m, 0.40m, 0.45m, 0.46m,
                [new MaintenanceMarginReductionItem(
                    "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell, 5, 1_000m, 2_000m)],
                t),
            new OrderApproved(decisionId, Intent(), 10, t),
            new OrderCancelled(decisionId, "ORD-1", "市場閉場のため取消", t),
            new OrderDispatchForgone(decisionId, Intent(), OrderDispatchForgoneReason.BrokerUnavailable, t),
            new OrderExecuted(decisionId, "ORD-1", OrderStatus.Filled, 10, 1_005m, t, BrokerProvider.MoomooSimulate),
            new OrderModified(decisionId, "ORD-1", 10, 1_000m, 8, 1_010m, "数量を縮小", t),
            new OrderRejected(decisionId, Intent(), [RejectionReason.PerOrderAmountExceeded], t),
            new PositionCloseRequested(
                decisionId, "AAPL", Market.UnitedStates, TradeSide.Sell, 10, 1_020m, "endazon", "利益確定", t),
            new PositionClosedWithStaleFxRate("7203", Market.Japan, "JPY", 300, 0.0067m, t.AddDays(-31), 31d, t),
            new PositionReconciliationDrift(
                [new PositionDriftItem("AAPL", Market.UnitedStates, 10, 8, PositionDriftKind.QuantityMismatch)], t, t),
            new PriceMovementDetected(Guid.NewGuid(), "AAPL", Market.UnitedStates, 1_050m, 1_000m, 0.05m, t),
            new ProtectiveStopCoverageLost(
                decisionId, "AAPL", Market.UnitedStates, ProtectiveStopLossCause.LapsedInFlight,
                ProtectiveStopRemediation.PositionClosed, 10, Guid.NewGuid(), Intent(), t),
            new ProtectiveStopPlaced(decisionId, Guid.NewGuid(), "STOP-1", Intent(), 950m, 1, t),
            new ReportConfirmed("2026-08-28", "Daily", "endazon", 3, t),
            new ReportDraftPresented("2026-08-28", "Daily", "2026-08-28（日報）", "本日の方針", 1, t),
            new StageTransitioned(1, 1, 2, "Promotion", "endazon", "基準を満たした", t, 100, false),
            new StopLossTriggered(
                Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 10, 950m, 960m, t),
            new TradeDecisionMade(decisionId, Intent(), "上昇トレンドのため買い", t),
            new TradeDecisionSkipped("trade-decision", "model-mismatch", "claude-opus-5", "claude-haiku-4-5", t),
            new TradeExpenseRecorded(new TradeExpense(
                "AAPL", Market.UnitedStates, TradeExpenseCategory.Commission, 1.00m, day, "ORD-1", t)),
            new WithdrawalTriggered(0, "最大 DD 到達", true, t),
        ];
    }
}
