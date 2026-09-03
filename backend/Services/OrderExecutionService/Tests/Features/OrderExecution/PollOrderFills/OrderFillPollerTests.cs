using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.PollOrderFills;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// #270, FR-05, FR-10, IADR-0113: 約定状態の追跡ポーリング。moomoo は発注時に Accepted（未約定）を返すため、
// 終端化するまで追い続けて記録を更新し、OrderExecuted を再発行して台帳（統制の入力）へ届ける。
public class OrderFillPollerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 6, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaxTracking = TimeSpan.FromHours(24);

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    // 注文 ID ごとに「照会したら返す状態」を差し替えられるブローカ。照会回数も数える。
    private sealed class FakeBroker : IBrokerAdapter
    {
        // #386, IADR-0149: ポーラーが再発行する OrderExecuted に載る発注先。
        public BrokerProvider Provider { get; init; } = BrokerProvider.MoomooSimulate;

        private readonly Dictionary<string, Func<BrokerOrder?>> _responses = [];

        public int QueryCount { get; private set; }

        public List<string> QueriedOrderIds { get; } = [];

        public void Respond(string orderId, BrokerOrder? order) => _responses[orderId] = () => order;

        public void Throw(string orderId, Exception ex) => _responses[orderId] = () => throw ex;

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default)
        {
            QueryCount++;
            QueriedOrderIds.Add(orderId);
            return Task.FromResult(_responses.TryGetValue(orderId, out var f) ? f() : null);
        }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new InvalidOperationException("ポーラーは発注しない（読み取り照会のみ）。");

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) =>
            throw new InvalidOperationException("ポーラーは取消しない（読み取り照会のみ）。");
    }

    private static OrderIntent Intent(int qty = 1_000, decimal price = 340m) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, qty, price);

    // moomoo 発注直後の記録（Accepted・約定 0）。#270 の出発点。
    private static ExecutionRecord Dispatched(string orderId, Guid decisionId, DateTimeOffset at, int qty = 1_000) =>
        new(decisionId, orderId, "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, qty, 340m, FilledQuantity: 0, AveragePrice: 0m,
            OrderStatus.Accepted, SlippageRatio: 0m, ExecutedAt: at);

    private static BrokerOrder Snapshot(
        string orderId, OrderStatus status, int filled, decimal avgPrice, DateTimeOffset? completedAt = null) =>
        new(orderId, Intent(), status, filled, avgPrice, PlacedAt: default, CompletedAt: completedAt);

    private static (OrderFillPoller Poller, InMemoryExecutedOrderStore Store, FakeBroker Broker) Build(
        DateTimeOffset? now = null)
    {
        var store = new InMemoryExecutedOrderStore();
        var broker = new FakeBroker();
        return (new OrderFillPoller(broker, store, new FakeClock(now ?? Now)), store, broker);
    }

    [Fact]
    public async Task 未約定の注文が全量約定したら記録を更新し約定を発行する()
    {
        var (poller, store, broker) = Build();
        var decisionId = Guid.NewGuid();
        store.Save(Dispatched("ORD-1", decisionId, Now.AddMinutes(-3)));
        broker.Respond("ORD-1", Snapshot("ORD-1", OrderStatus.Filled, 1_000, 341.7m, Now.AddMinutes(-1)));

        var result = await poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Scanned.Should().Be(1);
        result.Terminalized.Should().Be(1);
        result.Executed.Should().ContainSingle();
        var executed = result.Executed[0];
        executed.DecisionId.Should().Be(decisionId);
        executed.OrderId.Should().Be("ORD-1");
        executed.Status.Should().Be(OrderStatus.Filled);
        executed.FilledQuantity.Should().Be(1_000);
        executed.AveragePrice.Should().Be(341.7m);
        // 終端化した時刻はブローカの完了時刻を採る（発注時刻のままにしない）。
        executed.ExecutedAt.Should().Be(Now.AddMinutes(-1));

        var record = store.FindByDecisionId(decisionId)!;
        record.Status.Should().Be(OrderStatus.Filled);
        record.FilledQuantity.Should().Be(1_000);
        record.AveragePrice.Should().Be(341.7m);
        // FR-16: 実効スリッページは約定価格の確定後に算出し直す（発注時点の 0 のままにしない）。
        record.SlippageRatio.Should().Be(SlippageCalculator.Compute(340m, 341.7m, TradeSide.Buy));

        // FR-20, #386, IADR-0149 決定1: 再発行する OrderExecuted にも**照会したアダプタの発注先**が載る。
        // ここが欠けると、ポーラー経由で届いた約定（moomoo の非同期約定の主経路）が
        // 既定値 InternalPaper のまま Stage 1 の件数から落ちる。
        executed.Provider.Should().Be(BrokerProvider.MoomooSimulate);
        // 記録の Intent.Mode（段階の既定モード＝InternalPaper）ではないことを対照で固定する。
        executed.Provider.Should().NotBe(Intent().Mode);
    }

    [Fact]
    public async Task 部分約定は累積数量で反映し全量約定まで追跡する()
    {
        var (poller, store, broker) = Build();
        var decisionId = Guid.NewGuid();
        store.Save(Dispatched("ORD-1", decisionId, Now.AddMinutes(-3)));

        broker.Respond("ORD-1", Snapshot("ORD-1", OrderStatus.PartiallyFilled, 300, 340.5m));
        var first = await poller.PollOnceAsync(MaxTracking, batchSize: 100);

        first.Executed.Should().ContainSingle();
        first.Executed[0].FilledQuantity.Should().Be(300);
        first.Terminalized.Should().Be(0);
        // 非終端の進捗では発注時刻を保持する（当日集計の帰属を発注日から動かさない）。
        first.Executed[0].ExecutedAt.Should().Be(Now.AddMinutes(-3));

        // 部分約定のままなので次の巡回でも追跡対象に残る。
        broker.Respond("ORD-1", Snapshot("ORD-1", OrderStatus.Filled, 1_000, 340.8m, Now));
        var second = await poller.PollOnceAsync(MaxTracking, batchSize: 100);

        second.Executed.Should().ContainSingle();
        // moomoo の約定数量は累積値。差分ではなく累積のまま流す（台帳は OrderId 単位の単調 upsert で受ける）。
        second.Executed[0].FilledQuantity.Should().Be(1_000);
        second.Terminalized.Should().Be(1);
        store.FindByDecisionId(decisionId)!.FilledQuantity.Should().Be(1_000);
    }

    [Fact]
    public async Task 部分約定のまま取消された注文も約定分を保持して終端化する()
    {
        var (poller, store, broker) = Build();
        var decisionId = Guid.NewGuid();
        store.Save(Dispatched("ORD-1", decisionId, Now.AddMinutes(-3)));
        broker.Respond("ORD-1", Snapshot("ORD-1", OrderStatus.Cancelled, 400, 339.9m, Now));

        var result = await poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Terminalized.Should().Be(1);
        result.Executed[0].Status.Should().Be(OrderStatus.Cancelled);
        result.Executed[0].FilledQuantity.Should().Be(400);
    }

    [Fact]
    public async Task 状態も約定数も変わらなければ何も書かず発行もしない()
    {
        var (poller, store, broker) = Build();
        store.Save(Dispatched("ORD-1", Guid.NewGuid(), Now.AddMinutes(-3)));
        broker.Respond("ORD-1", Snapshot("ORD-1", OrderStatus.Accepted, 0, 0m));

        var result = await poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Scanned.Should().Be(1);
        result.Unchanged.Should().Be(1);
        result.Updated.Should().Be(0);
        result.Executed.Should().BeEmpty();
    }

    [Fact]
    public async Task 照会できない注文はブローカ状態を推測せず据え置く()
    {
        var (poller, store, broker) = Build();
        var decisionId = Guid.NewGuid();
        store.Save(Dispatched("ORD-1", decisionId, Now.AddMinutes(-3)));
        broker.Respond("ORD-1", null); // 当日一覧に無い・照会失敗（アダプタが null に倒す）

        var result = await poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Unknown.Should().Be(1);
        result.Executed.Should().BeEmpty();
        // 記録は非終端のまま＝次回巡回で再試行できる。
        var record = store.FindByDecisionId(decisionId)!;
        record.Status.Should().Be(OrderStatus.Accepted);
        store.FindPendingSince(Now - MaxTracking, 100).Should().ContainSingle();
    }

    [Fact]
    public async Task 照会が例外でも巡回は止まらず残りを処理する()
    {
        var (poller, store, broker) = Build();
        store.Save(Dispatched("ORD-1", Guid.NewGuid(), Now.AddMinutes(-5)));
        store.Save(Dispatched("ORD-2", Guid.NewGuid(), Now.AddMinutes(-3)));
        broker.Throw("ORD-1", new InvalidOperationException("照会失敗"));
        broker.Respond("ORD-2", Snapshot("ORD-2", OrderStatus.Filled, 1_000, 340m, Now));

        var result = await poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Failed.Should().Be(1);
        result.Terminalized.Should().Be(1);
        result.Executed.Should().ContainSingle(e => e.OrderId == "ORD-2");
    }

    [Fact]
    public async Task 約定数は巻き戻さない()
    {
        var (poller, store, broker) = Build();
        var decisionId = Guid.NewGuid();
        store.Save(Dispatched("ORD-1", decisionId, Now.AddMinutes(-3)) with
        {
            Status = OrderStatus.PartiallyFilled,
            FilledQuantity = 600,
            AveragePrice = 340.2m,
        });
        // 部分列挙・順序前後で少ない数量が返っても採らない（終端状態だけを採り約定数は保持する）。
        broker.Respond("ORD-1", Snapshot("ORD-1", OrderStatus.Cancelled, 100, 300m, Now));

        var result = await poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Executed[0].FilledQuantity.Should().Be(600);
        result.Executed[0].AveragePrice.Should().Be(340.2m);
        result.Executed[0].Status.Should().Be(OrderStatus.Cancelled);
        store.FindByDecisionId(decisionId)!.FilledQuantity.Should().Be(600);
    }

    [Fact]
    public async Task 終端の記録は照会しない()
    {
        var (poller, store, broker) = Build();
        store.Save(Dispatched("ORD-1", Guid.NewGuid(), Now.AddMinutes(-3)) with
        {
            Status = OrderStatus.Filled,
            FilledQuantity = 1_000,
            AveragePrice = 340m,
        });
        store.Save(Dispatched("ORD-2", Guid.NewGuid(), Now.AddMinutes(-2)) with { Status = OrderStatus.Rejected });

        var result = await poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Scanned.Should().Be(0);
        broker.QueryCount.Should().Be(0);
    }

    [Fact]
    public async Task 追跡上限を過ぎた記録は照会しない()
    {
        var (poller, store, broker) = Build();
        store.Save(Dispatched("ORD-OLD", Guid.NewGuid(), Now.AddHours(-25)));
        store.Save(Dispatched("ORD-NEW", Guid.NewGuid(), Now.AddHours(-1)));

        var result = await poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Scanned.Should().Be(1);
        broker.QueriedOrderIds.Should().Equal("ORD-NEW");
    }

    [Fact]
    public async Task 一巡回の件数は上限で打ち切る()
    {
        var (poller, store, broker) = Build();
        for (var i = 0; i < 5; i++)
            store.Save(Dispatched($"ORD-{i}", Guid.NewGuid(), Now.AddMinutes(-10 + i)));

        var result = await poller.PollOnceAsync(MaxTracking, batchSize: 2);

        result.Scanned.Should().Be(2);
        broker.QueryCount.Should().Be(2);
        // 古い順に処理する（滞留を後回しにしない）。
        broker.QueriedOrderIds.Should().Equal("ORD-0", "ORD-1");
    }

    [Fact]
    public async Task 終端時刻が取れないブローカ応答では巡回時刻を採る()
    {
        var (poller, store, broker) = Build();
        store.Save(Dispatched("ORD-1", Guid.NewGuid(), Now.AddMinutes(-3)));
        broker.Respond("ORD-1", Snapshot("ORD-1", OrderStatus.Filled, 1_000, 340m, completedAt: null));

        var result = await poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Executed[0].ExecutedAt.Should().Be(Now);
    }
}
