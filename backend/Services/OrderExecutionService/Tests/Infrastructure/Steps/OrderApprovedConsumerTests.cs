using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.RecordTradeExpenses;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.Metrics;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// FR-05, UC-01, UC-02: OrderApproved 購読 → ペーパー発注 → OrderExecuted 発行の検証。
//
// ADR-0013, IADR-0129, #354: MassTransit のテストハーネス（AddMassTransitTestHarness + harness.Consumed/Published）
// から Wolverine.Tracking（TrackActivity + session.Executed/Sent）へ移行した。表明の意味は同じ。
// 本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す。
public class OrderApprovedConsumerTests
{
    // 発注回数を数えるペーパーブローカ（再配送で二重発注しないことの確認用・#131）。
    // #331, IADR-0210: 保護逆指値（IProtectiveOrderBroker）は内側の paper に委譲する（未実装だと Open が見送られる）。
    private sealed class CountingPaperBroker : IBrokerAdapter, IProtectiveOrderBroker
    {
        private readonly PaperBrokerAdapter _inner = new();

        // #386, IADR-0149: 内蔵 paper をそのまま名乗る（Stage 1 には算入されない側）。
        public BrokerProvider Provider => BrokerProvider.InternalPaper;

        public int PlaceCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            PlaceCount++;
            return _inner.PlaceOrderAsync(intent, ct);
        }

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            _inner.PlaceStopOrderAsync(closeIntent, triggerPrice, decisionId, ct);

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) =>
            _inner.PlaceMarketOrderAsync(closeIntent, decisionId, ct);

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            _inner.GetOrderAsync(orderId, ct);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) =>
            _inner.CancelOrderAsync(orderId, ct);
    }

    // #331, IADR-0211: OpenD 接続不可（確実に未発注）を再現するブローカ。
    private sealed class UnavailableBroker : IBrokerAdapter, IProtectiveOrderBroker
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new BrokerUnavailableException("OpenD へ接続できません（テスト）");

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            throw new BrokerUnavailableException("OpenD へ接続できません（テスト）");

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) =>
            throw new BrokerUnavailableException("OpenD へ接続できません（テスト）");

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;
    }

    // #331, IADR-0210: 逆指値だけをブローカーが受理しないブローカ（エントリーと手仕舞いは paper に委譲）。
    private sealed class StopRejectingPaperBroker : IBrokerAdapter, IProtectiveOrderBroker
    {
        private readonly PaperBrokerAdapter _inner = new();

        public BrokerProvider Provider => _inner.Provider;

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            _inner.PlaceOrderAsync(intent, ct);

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            Task.FromResult(new BrokerOrder(
                Guid.NewGuid().ToString("N"), closeIntent, OrderStatus.Rejected, 0, 0m,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) =>
            _inner.PlaceMarketOrderAsync(closeIntent, decisionId, ct);

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            _inner.GetOrderAsync(orderId, ct);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) =>
            _inner.CancelOrderAsync(orderId, ct);
    }

    // FR-10, ADR-0040 決定1, #819, IADR-0342: moomoo SIMULATE を名乗り、発注は paper に委譲するブローカ
    // （S0 以外の手法は SIMULATE でしか適用されないため）。逆指値の発注回数を数える。
    private sealed class SimulatePaperBroker : IBrokerAdapter, IProtectiveOrderBroker
    {
        private readonly PaperBrokerAdapter _inner = new();

        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public int StopPlaceCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            _inner.PlaceOrderAsync(intent, ct);

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default)
        {
            StopPlaceCount++;
            return _inner.PlaceStopOrderAsync(closeIntent, triggerPrice, decisionId, ct);
        }

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) =>
            _inner.PlaceMarketOrderAsync(closeIntent, decisionId, ct);

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            _inner.GetOrderAsync(orderId, ct);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) =>
            _inner.CancelOrderAsync(orderId, ct);
    }

    // FR-10, ADR-0040 決定1（S3）, #821, IADR-0347: S3 の能力を持つ SIMULATE 相当のフェイク。
    // 代替注文種別は拒否される（公式は模擬取引を指値・成行のみとしている）。拒否理由つきで返す。
    private sealed class AlternativeStopRejectingSimulateBroker
        : IBrokerAdapter, IProtectiveOrderBroker, IAlternativeProtectiveOrderBroker
    {
        private readonly PaperBrokerAdapter _inner = new();

        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public AlternativeProtectiveOrderType AlternativeProtectiveOrderType =>
            AlternativeProtectiveOrderType.StopLimit;

        public int StopPlaceCount { get; private set; }

        public int AlternativePlaceCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            _inner.PlaceOrderAsync(intent, ct);

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default)
        {
            StopPlaceCount++;
            return _inner.PlaceStopOrderAsync(closeIntent, triggerPrice, decisionId, ct);
        }

        public Task<AlternativeProtectiveOrderPlacement> PlaceAlternativeStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, decimal entryReferencePrice, Guid decisionId,
            CancellationToken ct = default)
        {
            AlternativePlaceCount++;
            return Task.FromResult(new AlternativeProtectiveOrderPlacement(
                new BrokerOrder("alt-1", closeIntent, OrderStatus.Rejected, 0, 0m,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                AlternativeProtectiveOrderType.StopLimit,
                1,
                "Paper trading does not support StopLimit order",
                BrokerOrderId: null));
        }

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) =>
            _inner.PlaceMarketOrderAsync(closeIntent, decisionId, ct);

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            _inner.GetOrderAsync(orderId, ct);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) =>
            _inner.CancelOrderAsync(orderId, ct);
    }

    // 🔴 #864, IADR-0355: 建玉照会の能力を持つブローカー（moomoo 相当）。発注は paper に委譲し、
    // **建玉は空列（＝ブローカーは 1 株も持っていない）**を返す —— #849 で実測した乖離そのものである。
    private sealed class EmptyPositionBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        private readonly PaperBrokerAdapter _inner = new();

        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public int PlaceCount { get; private set; }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BrokerPositionSnapshot>?>([]);

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            PlaceCount++;
            return _inner.PlaceOrderAsync(intent, ct);
        }

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            _inner.PlaceStopOrderAsync(closeIntent, triggerPrice, decisionId, ct);

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) =>
            _inner.PlaceMarketOrderAsync(closeIntent, decisionId, ct);

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            _inner.GetOrderAsync(orderId, ct);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) =>
            _inner.CancelOrderAsync(orderId, ct);
    }

    private const string ServiceName = "ai-stock-trading.order-execution-service";

    private static Task<IHost> NewHostAsync(IExecutedOrderStore store, IBrokerAdapter broker) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton(broker);
                opts.Services.AddSingleton(store);
                // #131, IADR-0057: 発注前 DecisionId 予約（二重発注の防止）。
                opts.Services.AddSingleton<IOrderReservationStore, InMemoryOrderReservationStore>();
                // NFR-07, #287, IADR-0255: 業務メトリクスはハンドラの**必須依存**である。
                opts.Services.AddSingleton<BusinessMetrics>();
                opts.Services.AddSingleton<AppSvc>();
                // FR-11, #633, IADR-0300: 経費の記録もハンドラの必須依存である（既定は常に「取得できない」）。
                opts.Services.AddSingleton<IOrderExpenseSource, UnsuppliedOrderExpenseSource>();
                opts.Services.AddSingleton<TradeExpenseRecordingService>();

                opts.UseAiStockTradingRabbitMq(
                    ServiceName, "amqp://guest:guest@localhost:5672",
                    typeof(OrderApprovedHandler).Assembly);
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    // FR-10, #331: Open 注文は StopLossPrice 必須（無いと見送り）。
    private static OrderIntent NewIntent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 1_000m,
            StopLossPrice: 950m);

    [Fact]
    public async Task 承認注文を購読しOrderExecutedを発行する()
    {
        var store = new InMemoryExecutedOrderStore();
        using var host = await NewHostAsync(store, new PaperBrokerAdapter());

        var intent = NewIntent();
        var decisionId = Guid.NewGuid();
        var session = await host.TrackActivityForTest()
            .InvokeMessageAndWaitAsync(new OrderApproved(decisionId, intent, 10, DateTimeOffset.UtcNow));

        session.Executed.MessagesOf<OrderApproved>().Should().NotBeEmpty();
        session.Sent.MessagesOf<OrderExecuted>().Should().NotBeEmpty();
        var executed = session.Sent.MessagesOf<OrderExecuted>().First();
        executed.DecisionId.Should().Be(decisionId);
        executed.Status.Should().Be(OrderStatus.Filled);
        store.GetAll().Should().ContainSingle(r => r.DecisionId == decisionId);

        await host.StopAsync();
    }

    [Fact]
    public async Task 同一OrderApprovedが再配送されても二重発注しない()
    {
        // #131: メッセージングの再配送（2s/10s/30s の再試行）で同じ OrderApproved が再処理されても、
        // ブローカ発注・台帳計上は高々1回に限定される（バス経由の end-to-end で固定する）。
        var store = new InMemoryExecutedOrderStore();
        var broker = new CountingPaperBroker();
        using var host = await NewHostAsync(store, broker);

        var approved = new OrderApproved(Guid.NewGuid(), NewIntent(), 10, DateTimeOffset.UtcNow);
        var first = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(approved);
        first.Executed.MessagesOf<OrderApproved>().Should().NotBeEmpty();
        var second = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(approved); // 再配送

        first.Executed.MessagesOf<OrderApproved>().Concat(second.Executed.MessagesOf<OrderApproved>())
            .Should().HaveCount(2, "同じメッセージが2回処理されること");
        broker.PlaceCount.Should().Be(1);        // 二重発注しない
        store.GetAll().Should().ContainSingle(r => r.DecisionId == approved.DecisionId); // 二重計上しない

        await host.StopAsync();
    }

    // FR-10, #331, IADR-0210 決定2: エントリーと同時に保護逆指値が発注され、ProtectiveStopPlaced が発行される
    // （リスク管理が台帳の承認行へ結線するイベント）。
    [Fact]
    public async Task Open注文では保護逆指値が同時発注されProtectiveStopPlacedが発行される()
    {
        var store = new InMemoryExecutedOrderStore();
        using var host = await NewHostAsync(store, new PaperBrokerAdapter());

        var decisionId = Guid.NewGuid();
        var session = await host.TrackActivityForTest()
            .InvokeMessageAndWaitAsync(new OrderApproved(decisionId, NewIntent(), 10, DateTimeOffset.UtcNow));

        var placed = session.Sent.MessagesOf<ProtectiveStopPlaced>().Should().ContainSingle().Which;
        placed.EntryDecisionId.Should().Be(decisionId);
        placed.TriggerPrice.Should().Be(950m);
        placed.CloseIntent.Side.Should().Be(TradeSide.Sell);
        placed.CloseIntent.PositionEffect.Should().Be(PositionEffect.Close);
        // 逆指値レグも発注結果として記録され、約定追跡ポーリングの対象になる（IADR-0113 へ載せる）。
        store.GetAll().Should().Contain(r => r.DecisionId == placed.StopDecisionId);

        await host.StopAsync();
    }

    // FR-05, ADR-0002（SPOF）, #331, IADR-0211: OpenD 切断（確実に未発注）は**キューイングせず見送り＋通知**。
    // ハンドラは例外を投げず（＝Wolverine の再試行・error キュー滞留を作らず）、OrderDispatchForgone のみ発行する。
    [Fact]
    public async Task OpenD切断時はキューイングせず見送りイベントを発行する()
    {
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton<IBrokerAdapter>(new UnavailableBroker());
                opts.Services.AddSingleton<IExecutedOrderStore>(store);
                opts.Services.AddSingleton<IOrderReservationStore>(reservations);
                // NFR-07, #287, IADR-0255: 業務メトリクスはハンドラの**必須依存**である。
                opts.Services.AddSingleton<BusinessMetrics>();
                opts.Services.AddSingleton<AppSvc>();
                // FR-11, #633, IADR-0300: 経費の記録もハンドラの必須依存である（既定は常に「取得できない」）。
                opts.Services.AddSingleton<IOrderExpenseSource, UnsuppliedOrderExpenseSource>();
                opts.Services.AddSingleton<TradeExpenseRecordingService>();
                opts.UseAiStockTradingRabbitMq(
                    ServiceName, "amqp://guest:guest@localhost:5672", typeof(OrderApprovedHandler).Assembly);
                opts.StubAllExternalTransports();
            })
            .StartAsync();

        var approved = new OrderApproved(Guid.NewGuid(), NewIntent(), 10, DateTimeOffset.UtcNow);
        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(approved);

        // 例外にならず正常終了し（＝再試行キューに入らない）、見送りイベントだけが発行される。
        var forgone = session.Sent.MessagesOf<OrderDispatchForgone>().Should().ContainSingle().Which;
        forgone.DecisionId.Should().Be(approved.DecisionId);
        forgone.Reason.Should().Be(OrderDispatchForgoneReason.BrokerUnavailable);
        session.Sent.MessagesOf<OrderExecuted>().Should().BeEmpty("発注していないため注文状態は存在しない");
        store.GetAll().Should().BeEmpty("発注していない注文の記録を残さない");
        reservations.Find(approved.DecisionId)!.State.Should().Be(
            OrderDispatchState.Forgone, "確実に未発注でも予約は削除せず見送りの終端にする（#876。再配送で送らない）");

        await host.StopAsync();
    }

    // 🔴 T-10-503, FR-10, FR-05, FR-09, FR-11, ADR-0016, #864, IADR-0355: **台帳に建玉があり、ブローカーに無い決済。**
    // 本番と同じ配線（ハンドラ・DI・発行）で、①売り注文が 1 本も出ない ②見送りが出る
    // ③**既存の乖離検知と同じイベント**が出る（監査台帳と Critical 通知の入口。新しい経路を作らない）。
    [Fact]
    public async Task ブローカーに建玉が無い決済は本番配線でも発注されず乖離が発行される()
    {
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var broker = new EmptyPositionBroker();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, SystemClock>();
                opts.Services.AddSingleton<IBrokerAdapter>(broker);
                opts.Services.AddSingleton<IBrokerPositionSource>(broker);
                opts.Services.AddSingleton<IExecutedOrderStore>(store);
                opts.Services.AddSingleton<IOrderReservationStore>(reservations);
                opts.Services.AddSingleton<BusinessMetrics>();
                opts.Services.AddSingleton<AppSvc>();
                opts.Services.AddSingleton<IOrderExpenseSource, UnsuppliedOrderExpenseSource>();
                opts.Services.AddSingleton<TradeExpenseRecordingService>();
                opts.UseAiStockTradingRabbitMq(
                    ServiceName, "amqp://guest:guest@localhost:5672", typeof(OrderApprovedHandler).Assembly);
                opts.StubAllExternalTransports();
            })
            .StartAsync();

        // #849 の実測（台帳 3,381 株 / ブローカー 0 株）を決済として流す。
        var closeIntent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
            3_381, 100m, PositionEffect.Close);
        var approved = new OrderApproved(Guid.NewGuid(), closeIntent, 3_381, DateTimeOffset.UtcNow);

        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(approved);

        broker.PlaceCount.Should().Be(0, "保有 0 からの売り（裸のショート）を出してはならない");
        session.Sent.MessagesOf<OrderExecuted>().Should().BeEmpty();
        session.Sent.MessagesOf<OrderDispatchForgone>().Should().ContainSingle()
            .Which.Reason.Should().Be(OrderDispatchForgoneReason.BrokerPositionAbsent);
        var drift = session.Sent.MessagesOf<PositionReconciliationDrift>().Should().ContainSingle().Which;
        drift.Drifts.Should().ContainSingle().Which.BrokerQuantity.Should().Be(0);
        store.GetAll().Should().BeEmpty();
        reservations.Find(approved.DecisionId)!.State.Should().Be(
            OrderDispatchState.Forgone, "送らないと決めた注文は予約を取らず、見送りの記録だけを残す（#876）");

        await host.StopAsync();
    }

    // FR-10, #331, IADR-0210 決定3: 逆指値が未受理でも**逆指値なしの建玉を残さない**。
    // 約定済みのエントリーは成行で手仕舞い、ProtectiveStopCoverageLost を発行する。
    // 🔴 発行が欠けると、監査にも Critical 通知にも「なぜ建玉が消えたか」が残らない。
    [Fact]
    public async Task 逆指値が未受理なら建玉を解消しProtectiveStopCoverageLostが発行される()
    {
        var store = new InMemoryExecutedOrderStore();
        using var host = await NewHostAsync(store, new StopRejectingPaperBroker());

        var decisionId = Guid.NewGuid();
        var session = await host.TrackActivityForTest()
            .InvokeMessageAndWaitAsync(new OrderApproved(decisionId, NewIntent(), 10, DateTimeOffset.UtcNow));

        session.Sent.MessagesOf<ProtectiveStopPlaced>().Should().BeEmpty("逆指値は受理されていない");
        var lost = session.Sent.MessagesOf<ProtectiveStopCoverageLost>().Should().ContainSingle().Which;
        lost.EntryDecisionId.Should().Be(decisionId);
        lost.Cause.Should().Be(ProtectiveStopLossCause.RejectedAtEntry);
        lost.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed,
            "paper のエントリーは即時約定するため、建玉は成行で手仕舞われる");
        lost.CloseIntent!.PositionEffect.Should().Be(PositionEffect.Close);
        // 手仕舞いレグも記録され、台帳の建玉を減らす経路（OrderExecuted 相関）に載る。
        store.GetAll().Should().Contain(r => r.DecisionId == lost.CloseDecisionId);

        await host.StopAsync();
    }

    // FR-10, FR-12, ADR-0040 決定1（S2）, #819, IADR-0342 決定6: SIMULATE＋S2 の新規買いは保護逆指値を発注せず、
    // 免除の事実（ProtectiveStopWaived）だけが発行される。🔴 発行が欠けると監査にも通知にも
    // 「逆指値なしの建玉が意図して存在する」ことが残らない。
    [Fact]
    public async Task SIMULATEでS2の新規買いは保護逆指値を発注せずProtectiveStopWaivedが発行される()
    {
        var store = new InMemoryExecutedOrderStore();
        var broker = new SimulatePaperBroker();
        using var host = await NewHostAsync(store, broker);

        var decisionId = Guid.NewGuid();
        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderApproved(
            decisionId, NewIntent(), 10, DateTimeOffset.UtcNow,
            StopLossMethod: StopLossExecutionMethod.NoProtectiveStop));

        broker.StopPlaceCount.Should().Be(0, "S2 は保護レグを発注しない");
        session.Sent.MessagesOf<OrderExecuted>().Should().ContainSingle();
        session.Sent.MessagesOf<ProtectiveStopPlaced>().Should().BeEmpty();
        session.Sent.MessagesOf<ProtectiveStopCoverageLost>().Should().BeEmpty("建玉は解消しない");
        var waived = session.Sent.MessagesOf<ProtectiveStopWaived>().Should().ContainSingle().Which;
        waived.EntryDecisionId.Should().Be(decisionId);
        waived.Method.Should().Be(StopLossExecutionMethod.NoProtectiveStop);
        waived.Provider.Should().Be(BrokerProvider.MoomooSimulate);

        await host.StopAsync();
    }

    // FR-10, ADR-0040 決定1, #819, IADR-0342 決定4: S0 以外が SIMULATE 以外（ここでは内蔵 paper）へ届いたら
    // 発注せず、手法による見送りとして発行する。
    [Fact]
    public async Task SIMULATE以外へS2の承認が届いたら発注せず手法による見送りを発行する()
    {
        var store = new InMemoryExecutedOrderStore();
        var broker = new CountingPaperBroker();
        using var host = await NewHostAsync(store, broker);

        var approved = new OrderApproved(
            Guid.NewGuid(), NewIntent(), 10, DateTimeOffset.UtcNow,
            StopLossMethod: StopLossExecutionMethod.NoProtectiveStop);
        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(approved);

        broker.PlaceCount.Should().Be(0);
        session.Sent.MessagesOf<OrderDispatchForgone>().Should().ContainSingle()
            .Which.Reason.Should().Be(OrderDispatchForgoneReason.StopLossMethodNotPermitted);
        session.Sent.MessagesOf<OrderExecuted>().Should().BeEmpty();
        session.Sent.MessagesOf<ProtectiveStopWaived>().Should().BeEmpty();

        await host.StopAsync();
    }

    // FR-10, FR-11, FR-12, ADR-0040 決定1（S3）, #821, IADR-0347: SIMULATE＋S3 の発注で、
    // **注文種別と拒否理由（retType / retMsg）が AlternativeProtectiveStopAttempted として発行される**。
    // 🔴 発行が欠けると、監査台帳に「なぜ S3 が使えないのか」が 1 文字も残らない（#821 の目的そのもの）。
    [Fact]
    public async Task SIMULATEでS3の新規買いは代替注文種別の試行と拒否理由が発行される()
    {
        var store = new InMemoryExecutedOrderStore();
        var broker = new AlternativeStopRejectingSimulateBroker();
        using var host = await NewHostAsync(store, broker);

        var decisionId = Guid.NewGuid();
        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new OrderApproved(
            decisionId, NewIntent(), 10, DateTimeOffset.UtcNow,
            StopLossMethod: StopLossExecutionMethod.AlternativeBrokerOrderType));

        broker.AlternativePlaceCount.Should().Be(1, "S3 は代替注文種別で発注する");
        broker.StopPlaceCount.Should().Be(0, "S0 の逆指値は使わない");

        var attempted = session.Sent.MessagesOf<AlternativeProtectiveStopAttempted>()
            .Should().ContainSingle().Which;
        attempted.EntryDecisionId.Should().Be(decisionId);
        attempted.OrderType.Should().Be(AlternativeProtectiveOrderType.StopLimit);
        attempted.RejectReasonCode.Should().Be(1);
        attempted.RejectReasonMessage.Should().Be("Paper trading does not support StopLimit order");
        attempted.Method.Should().Be(StopLossExecutionMethod.AlternativeBrokerOrderType);

        // 拒否時の扱いは S0 と同じ（paper のエントリーは即時約定するため成行で手仕舞う）。
        session.Sent.MessagesOf<ProtectiveStopPlaced>().Should().BeEmpty();
        session.Sent.MessagesOf<ProtectiveStopWaived>().Should().BeEmpty("S3 は免除ではない");
        session.Sent.MessagesOf<ProtectiveStopCoverageLost>().Should().ContainSingle()
            .Which.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed);

        await host.StopAsync();
    }

    // NFR-07, FR-05, #287, IADR-0255: 発注が通ったとき、発注結果メトリクス（status・provider）が実際に刻まれる（肯定形）。
    [Fact]
    public async Task 発注が通ると発注結果メトリクスが実際に刻まれる()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var host = await NewHostAsync(new InMemoryExecutedOrderStore(), new PaperBrokerAdapter());

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new OrderApproved(Guid.NewGuid(), NewIntent(), 10, DateTimeOffset.UtcNow));

        capture.TagValuesOf(BusinessMetricNames.OrderExecutions, BusinessMetricNames.TagProvider)
            .Should().Contain(nameof(BrokerProvider.InternalPaper));
        capture.ValuesOf(BusinessMetricNames.OrderExecutions).Should().NotBeEmpty();

        await host.StopAsync();
    }

    // NFR-07, FR-05, FR-10, #287, IADR-0255: **見送りは注文状態を持たない**ため別の計器で数える（対の肯定形）。
    // ブローカーの拒否（OrderStatus.Rejected）へ混ぜると、集計が接続障害で汚染される。
    [Fact]
    public async Task 発注見送りは別の計器で刻まれる()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var host = await NewHostAsync(new InMemoryExecutedOrderStore(), new UnavailableBroker());

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new OrderApproved(Guid.NewGuid(), NewIntent(), 10, DateTimeOffset.UtcNow));

        capture.TagValuesOf(BusinessMetricNames.OrderDispatchForgone, BusinessMetricNames.TagReason)
            .Should().Contain(nameof(OrderDispatchForgoneReason.BrokerUnavailable));

        await host.StopAsync();
    }

    // NFR-01, #689, IADR-0307: **発注完了（OrderExecuted の発行）が NFR-01 の終点である。**
    // 承認が運んできた起点（価格変動検知の時刻）との差が端点間所要として刻まれ、
    // 起点そのものも OrderExecuted へ中継される（記録完了＝NFR-02 の終点で使うため）。
    [Fact]
    public async Task 起点を運ぶ承認は発注完了までの端点間所要を刻み起点を発注結果へ中継する()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var host = await NewHostAsync(new InMemoryExecutedOrderStore(), new PaperBrokerAdapter());

        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var session = await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new OrderApproved(
                Guid.NewGuid(), NewIntent(), 10, DateTimeOffset.UtcNow,
                BusinessMetrics.TriggerPriceMovement, startedAt));

        var executed = session.Sent.MessagesOf<OrderExecuted>().Should().ContainSingle().Which;
        executed.CycleTrigger.Should().Be(BusinessMetrics.TriggerPriceMovement);
        executed.CycleStartedAt.Should().Be(startedAt);

        capture.TagValuesOf(
                BusinessMetricNames.TradeCycleOrderCompletionLatencyMs, BusinessMetricNames.TagTrigger)
            .Should().Contain(BusinessMetrics.TriggerPriceMovement);
        capture.ValuesOf(BusinessMetricNames.TradeCycleOrderCompletionLatencyMs)
            .Should().Contain(m => m.Value > 0);

        await host.StopAsync();
    }

    // 🔴 NFR-01, #689, IADR-0307 決定3/4: **取引サイクル起点を持たない承認**（利用者の手仕舞い・
    // 維持証拠金の自動縮小）は、0 ms ではなく**未観測**として数える。0 を刻むと「5 分以内」を
    // 満たしているように見える。値を刻まないことの表明は BusinessMetricsTests（否定形の集約先）に置く。
    [Fact]
    public async Task 起点を持たない承認は未観測として数える()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var host = await NewHostAsync(new InMemoryExecutedOrderStore(), new PaperBrokerAdapter());

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new OrderApproved(Guid.NewGuid(), NewIntent(), 10, DateTimeOffset.UtcNow));

        capture.ValuesOf(BusinessMetricNames.TradeCycleLatencyUnobserved)
            .Should().Contain(m =>
                m.Tags[BusinessMetricNames.TagStage] == BusinessMetrics.StageOrderCompletion &&
                m.Tags[BusinessMetricNames.TagReason] == BusinessMetrics.UnobservedOriginMissing);

        await host.StopAsync();
    }
}
