using AiStockTrading.OrderExecution.Application.Adapters;
using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Infrastructure.Composable.Steps;
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
using AppSvc = AiStockTrading.OrderExecution.Application.Services.OrderExecutionAppService;

namespace AiStockTrading.OrderExecution.Infrastructure.Tests;

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
        reservations.Find(approved.DecisionId).Should().BeNull("確実に未発注のため予約は解放される");

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
}
