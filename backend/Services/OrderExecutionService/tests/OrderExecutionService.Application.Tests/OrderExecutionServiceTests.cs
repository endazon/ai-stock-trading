using OrderExecutionService.Application.Adapters;
using OrderExecutionService.Application.Ports;
using OrderExecutionService.Application.Services;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using AwesomeAssertions;
using Xunit;
using AppSvc = OrderExecutionService.Application.Services.OrderExecutionAppService;

namespace OrderExecutionService.Application.Tests;

// FR-05, UC-01, UC-02, IADR-0007/0016: 発注執行（承認→発注→約定確定・スリッページ記録）の検証。
public class OrderExecutionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 6, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // 参照の度に時刻が進むクロック。予約時刻と約定時刻が別々に取得されることの確認に使う。
    private sealed class AdvancingClock(DateTimeOffset start, TimeSpan step) : IClock
    {
        private DateTimeOffset _current = start - step;

        public DateTimeOffset UtcNow => _current = _current.Add(step);
    }

    // 任意の BrokerOrder を返すテスト用ブローカ（スリッページ等の制御用）。発注回数を数える。
    // onPlace は「発注が起きたその瞬間」の観測用（予約が発注前にコミットされることの確認に使う）。
    // FR-10, #331, IADR-0210: 保護逆指値（IProtectiveOrderBroker）も実装する——実装しないと Open 注文が
    // 見送られる（fail-closed）。既定は逆指値を滞留 Accepted で受理する。
    private sealed class FakeBroker(BrokerOrder order, Action? onPlace = null) : IBrokerAdapter, IProtectiveOrderBroker
    {
        // #386, IADR-0149: 既定は moomoo SIMULATE（Stage 1 の発注先）。paper 経路は個別に差し替える。
        public BrokerProvider Provider { get; init; } = BrokerProvider.MoomooSimulate;

        public int PlaceCount { get; private set; }
        public int StopPlaceCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            PlaceCount++;
            onPlace?.Invoke();
            return Task.FromResult(order);
        }

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default)
        {
            StopPlaceCount++;
            return Task.FromResult(new BrokerOrder(
                Guid.NewGuid().ToString("N"), closeIntent, OrderStatus.Accepted, 0, 0m, Now, null));
        }

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) =>
            Task.FromResult(new BrokerOrder(
                Guid.NewGuid().ToString("N"), closeIntent, OrderStatus.Filled,
                closeIntent.Quantity, closeIntent.Price, Now, Now));

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default)
            => Task.FromResult<BrokerOrder?>(order);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;
    }

    // #141, IADR-0092: client order id（DecisionId）伝播に対応するブローカ。伝播された DecisionId を記録する。
    private sealed class FakeCorrelatingBroker(BrokerOrder order)
        : IBrokerAdapter, IClientOrderIdBroker, IProtectiveOrderBroker
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public Guid? PropagatedDecisionId { get; private set; }
        public int PlainPlaceCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, Guid decisionId, CancellationToken ct = default)
        {
            PropagatedDecisionId = decisionId;
            return Task.FromResult(order);
        }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            PlainPlaceCount++; // client order id 対応ブローカでは呼ばれてはならない経路（回帰検出用）。
            return Task.FromResult(order);
        }

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            Task.FromResult(new BrokerOrder(
                Guid.NewGuid().ToString("N"), closeIntent, OrderStatus.Accepted, 0, 0m, Now, null));

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) =>
            Task.FromResult(new BrokerOrder(
                Guid.NewGuid().ToString("N"), closeIntent, OrderStatus.Filled,
                closeIntent.Quantity, closeIntent.Price, Now, Now));

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default)
            => Task.FromResult<BrokerOrder?>(order);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;
    }

    // Save を指定回数だけ失敗させるストア。「ブローカ発注成功 → 永続化失敗」の窓（#131）を再現する。
    private sealed class FlakySaveStore : IExecutedOrderStore
    {
        private readonly InMemoryExecutedOrderStore _inner = new();

        public int RemainingFailures { get; set; }

        public void Save(ExecutionRecord record)
        {
            if (RemainingFailures > 0)
            {
                RemainingFailures--;
                throw new InvalidOperationException("永続化失敗（発注成功→保存失敗の窓を再現）");
            }

            _inner.Save(record);
        }

        public IReadOnlyList<ExecutionRecord> GetAll() => _inner.GetAll();

        public ExecutionRecord? FindByDecisionId(Guid decisionId) => _inner.FindByDecisionId(decisionId);

        public IReadOnlyList<ExecutionRecord> FindPendingSince(DateTimeOffset since, int batchSize)
            => _inner.FindPendingSince(since, batchSize);

        public bool UpdateOutcome(
            string orderId, OrderStatus status, int filledQuantity, decimal averagePrice,
            decimal slippageRatio, DateTimeOffset executedAt)
            => _inner.UpdateOutcome(orderId, status, filledQuantity, averagePrice, slippageRatio, executedAt);
    }

    // MarkCompleted を指定回数だけ失敗させる予約ストア（Save 成功 → 確定失敗の窓を再現する）。
    private sealed class FlakyCompleteReservationStore : IOrderReservationStore
    {
        private readonly InMemoryOrderReservationStore _inner = new();

        public int RemainingFailures { get; set; }

        public bool TryReserve(Guid decisionId, DateTimeOffset reservedAt) => _inner.TryReserve(decisionId, reservedAt);

        public void MarkCompleted(Guid decisionId, string brokerOrderId, DateTimeOffset completedAt)
        {
            if (RemainingFailures > 0)
            {
                RemainingFailures--;
                throw new InvalidOperationException("確定失敗（保存成功→確定失敗の窓を再現）");
            }

            _inner.MarkCompleted(decisionId, brokerOrderId, completedAt);
        }

        public OrderDispatchReservation? Find(Guid decisionId) => _inner.Find(decisionId);

        // #137/#141, IADR-0059/0074: 本テストの関心事ではないが、ポート実装として委譲する。
        public IReadOnlyList<OrderDispatchReservation> FindStalledReserved(DateTimeOffset reservedBefore, int batchSize) =>
            _inner.FindStalledReserved(reservedBefore, batchSize);

        public bool Release(Guid decisionId) => _inner.Release(decisionId);

        public int PurgeCompletedBefore(DateTimeOffset cutoff, int batchSize) =>
            _inner.PurgeCompletedBefore(cutoff, batchSize);
    }

    private static AppSvc NewService(
        IBrokerAdapter broker, IExecutedOrderStore store, IOrderReservationStore? reservations = null) =>
        new(broker, store, reservations ?? new InMemoryOrderReservationStore(), new FakeClock());

    private static OrderApproved Approved(OrderIntent intent) =>
        new(Guid.NewGuid(), intent, intent.Quantity, Now);

    // FR-10, #331, IADR-0210: Open 注文は StopLossPrice が必須（無いと見送り＝fail-closed）。既定で持たせる。
    private static OrderIntent Intent(int qty = 10, decimal price = 1_000m,
        PositionEffect effect = PositionEffect.Open, TradeSide side = TradeSide.Buy) =>
        new("AAPL", Market.UnitedStates, side, ProductType.Cash, BrokerProvider.InternalPaper, qty, price, effect,
            StopLossPrice: effect == PositionEffect.Open ? 950m : null);

    [Fact]
    public async Task 承認注文はペーパーで約定しOrderExecutedを返す()
    {
        var store = new InMemoryExecutedOrderStore();
        var service = NewService(new PaperBrokerAdapter(), store);
        var approved = Approved(Intent());

        var result = await service.ExecuteAsync(approved);

        var executed = result.Executed!;
        executed.Status.Should().Be(OrderStatus.Filled);
        executed.FilledQuantity.Should().Be(10);
        executed.AveragePrice.Should().Be(1_000m);
        executed.DecisionId.Should().Be(approved.DecisionId);
        store.GetAll().Should().ContainSingle(r => r.DecisionId == approved.DecisionId);
    }

    // FR-20, FR-12, #386, IADR-0149 決定1: OrderExecuted は**実際に発注したアダプタの発注先**を運ぶ。
    // 承認 Intent の Mode（＝段階が定める既定の発注先。Stage 1 では常に SIMULATE）ではない——
    // intent.Mode を載せる実装だと、内蔵 paper で稼働していても Stage 1 の合格証跡へ積み上がる。
    [Fact]
    public async Task OrderExecutedは実際に発注したアダプタの発注先を運ぶ()
    {
        // 承認 Intent の Mode を SIMULATE にしておく。発注先は paper アダプタであり、載るべきは paper 側である。
        var simulateIntent = Intent() with { Mode = BrokerProvider.MoomooSimulate };

        var paper = (await NewService(new PaperBrokerAdapter(), new InMemoryExecutedOrderStore())
            .ExecuteAsync(Approved(simulateIntent))).Executed!;

        paper.Provider.Should().Be(
            BrokerProvider.InternalPaper, "発注先は intent.Mode ではなく実際に発注したアダプタが決める");

        // 対照: SIMULATE を名乗るアダプタなら SIMULATE が載る（常に paper を返す実装では緑にならない）。
        var order = new BrokerOrder("ORD-SIM", simulateIntent, OrderStatus.Filled, 10, 1_000m, Now, Now);
        var simulate = (await NewService(
                new FakeBroker(order) { Provider = BrokerProvider.MoomooSimulate },
                new InMemoryExecutedOrderStore())
            .ExecuteAsync(Approved(simulateIntent))).Executed!;

        simulate.Provider.Should().Be(BrokerProvider.MoomooSimulate);
    }

    [Fact]
    public async Task ブローカ拒否はOrderExecutedのRejectedになる()
    {
        // IADR-0007: 数量 0 は実ブローカが拒否する不正注文 → Rejected（発注前拒否 OrderRejected とは別）。
        var store = new InMemoryExecutedOrderStore();
        var service = NewService(new PaperBrokerAdapter(), store);

        var executed = (await service.ExecuteAsync(Approved(Intent(qty: 0)))).Executed!;

        executed.Status.Should().Be(OrderStatus.Rejected);
        executed.FilledQuantity.Should().Be(0);
        store.GetAll().Single().SlippageRatio.Should().Be(0m); // 未約定はスリッページ 0（保護レグも張られない）
    }

    [Fact]
    public async Task Close注文も同一経路で約定する()
    {
        var store = new InMemoryExecutedOrderStore();
        var service = NewService(new PaperBrokerAdapter(), store);

        var executed = (await service.ExecuteAsync(
            Approved(Intent(effect: PositionEffect.Close, side: TradeSide.Sell)))).Executed!;

        executed.Status.Should().Be(OrderStatus.Filled);
        store.GetAll().Single().PositionEffect.Should().Be(PositionEffect.Close);
    }

    [Fact]
    public async Task スリッページが取引毎に算出され記録される()
    {
        // 計画 1000 の買いが 1005 で約定 → +0.5% の不利スリッページ。
        var store = new InMemoryExecutedOrderStore();
        var intent = Intent(price: 1_000m);
        var brokerOrder = new BrokerOrder("o1", intent, OrderStatus.Filled, 10, 1_005m, Now, Now);
        var service = NewService(new FakeBroker(brokerOrder), store);

        var approved = Approved(intent);
        await service.ExecuteAsync(approved);

        store.GetAll().Single(r => r.DecisionId == approved.DecisionId).SlippageRatio.Should().Be(0.005m);
    }

    [Fact]
    public async Task 同一DecisionIdの再処理では再発注せず既存結果を返す()
    {
        // 冪等性: メッセージングの再配送で同一 OrderApproved が再処理されても二重発注・二重計上しない。
        var store = new InMemoryExecutedOrderStore();
        var intent = Intent();
        var broker = new FakeBroker(new BrokerOrder("o1", intent, OrderStatus.Filled, 10, 1_000m, Now, Now));
        var service = NewService(broker, store);
        var approved = Approved(intent);

        var first = await service.ExecuteAsync(approved);
        var second = await service.ExecuteAsync(approved); // 再処理

        broker.PlaceCount.Should().Be(1);          // 再発注しない
        broker.StopPlaceCount.Should().Be(1);      // 保護レグも再発注しない（#331）
        store.GetAll().Where(r => r.DecisionId == approved.DecisionId)
            .Should().ContainSingle();              // 二重計上しない
        second.Executed!.OrderId.Should().Be(first.Executed!.OrderId); // 既存結果を返す
    }

    // ---- #131 / IADR-0057: 発注前 DecisionId 予約による冪等化（予約 → 発注 → 確定の3相）----

    [Fact]
    public async Task 発注成功後の永続化失敗を跨いでも再発注しない()
    {
        // #131 の中核。ブローカ発注は成功したが Save 前に落ちた窓では executed_orders に記録が無いため、
        // 発注後の DecisionId 照合だけでは再配送時に二重発注し得た。発注前予約がこの窓を塞ぐ。
        var store = new FlakySaveStore { RemainingFailures = 1 };
        var reservations = new InMemoryOrderReservationStore();
        var intent = Intent();
        var broker = new FakeBroker(new BrokerOrder("o1", intent, OrderStatus.Filled, 10, 1_000m, Now, Now));
        var service = NewService(broker, store, reservations);
        var approved = Approved(intent);

        // 1回目: 発注は成功するが永続化で落ちる（プロセス断相当）。
        var first = async () => await service.ExecuteAsync(approved);
        await first.Should().ThrowAsync<InvalidOperationException>();
        broker.PlaceCount.Should().Be(1);

        // 再配送: 予約が Reserved のまま残る＝発注済みか不明。安全側に倒して再発注しない（at-most-once）。
        var redelivered = async () => await service.ExecuteAsync(approved);
        await redelivered.Should().ThrowAsync<OrderDispatchReservationConflictException>();

        broker.PlaceCount.Should().Be(1);   // 二重発注しない（受け入れ基準）
        store.GetAll().Should().BeEmpty();  // 二重計上もしない
    }

    [Fact]
    public async Task 予約はブローカ発注の前にコミットされる()
    {
        // 予約が発注より後だと、発注成功→予約失敗の窓が新たに空く。順序そのものを固定する。
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var intent = Intent();
        var approved = Approved(intent);
        OrderDispatchReservation? seenAtPlace = null;
        var broker = new FakeBroker(
            new BrokerOrder("o1", intent, OrderStatus.Filled, 10, 1_000m, Now, Now),
            onPlace: () => seenAtPlace = reservations.Find(approved.DecisionId));
        var service = NewService(broker, store, reservations);

        await service.ExecuteAsync(approved);

        seenAtPlace.Should().NotBeNull("ブローカ発注の時点で予約がコミット済みであること");
        seenAtPlace!.State.Should().Be(OrderDispatchState.Reserved);
    }

    [Fact]
    public async Task 予約済み未確定の再処理は発注せず拒否する()
    {
        // 発注前に落ちた場合も「発注したか不明」と区別が付かないため、同様に拒否する（IADR-0057）。
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var intent = Intent();
        var broker = new FakeBroker(new BrokerOrder("o1", intent, OrderStatus.Filled, 10, 1_000m, Now, Now));
        var service = NewService(broker, store, reservations);
        var approved = Approved(intent);
        reservations.TryReserve(approved.DecisionId, Now).Should().BeTrue();

        var act = async () => await service.ExecuteAsync(approved);

        (await act.Should().ThrowAsync<OrderDispatchReservationConflictException>())
            .Which.DecisionId.Should().Be(approved.DecisionId);
        broker.PlaceCount.Should().Be(0);
    }

    [Fact]
    public async Task 保存成功後の確定失敗を跨いだ再処理は既存結果を再発行する()
    {
        // Save は済んでいるため結果は確定している。予約が Reserved に取り残されていても、
        // 完了の権威（executed_orders）が先に効いて既存結果を再発行する（拒否しない）。
        var store = new InMemoryExecutedOrderStore();
        var reservations = new FlakyCompleteReservationStore { RemainingFailures = 1 };
        var intent = Intent();
        var broker = new FakeBroker(new BrokerOrder("o1", intent, OrderStatus.Filled, 10, 1_000m, Now, Now));
        var service = NewService(broker, store, reservations);
        var approved = Approved(intent);

        var first = async () => await service.ExecuteAsync(approved);
        await first.Should().ThrowAsync<InvalidOperationException>();

        var second = await service.ExecuteAsync(approved); // 再配送

        second.Executed!.OrderId.Should().Be("o1");
        broker.PlaceCount.Should().Be(1);        // 再発注しない
        store.GetAll().Where(r => r.DecisionId == approved.DecisionId)
            .Should().ContainSingle();           // 二重計上しない
    }

    [Fact]
    public async Task 完了した発注の予約はブローカ注文IDつきで確定される()
    {
        // 予約の Completed 状態と BrokerOrderId は、stuck 予約（＝要リコンサイル）と完了済みを
        // 運用上区別するために残す（自動リコンサイルは後続 issue）。
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var intent = Intent();
        var service = NewService(
            new FakeBroker(new BrokerOrder("o1", intent, OrderStatus.Filled, 10, 1_000m, Now, Now)), store, reservations);
        var approved = Approved(intent);

        await service.ExecuteAsync(approved);

        var reservation = reservations.Find(approved.DecisionId);
        reservation.Should().NotBeNull();
        reservation!.State.Should().Be(OrderDispatchState.Completed);
        reservation.BrokerOrderId.Should().Be("o1");
    }

    [Fact]
    public async Task 約定時刻はブローカ往復の後に記録される()
    {
        // 予約時刻を約定時刻へ流用すると、往復の実時間が記録から消えて監査・リコンサイルを誤らせる。
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var intent = Intent();
        var service = new AppSvc(
            new FakeBroker(new BrokerOrder("o1", intent, OrderStatus.Filled, 10, 1_000m, Now, Now)),
            store, reservations, new AdvancingClock(Now, TimeSpan.FromSeconds(1)));
        var approved = Approved(intent);

        var executed = (await service.ExecuteAsync(approved)).Executed!;

        executed.ExecutedAt.Should().BeAfter(reservations.Find(approved.DecisionId)!.ReservedAt);
    }

    // ---- #141 / IADR-0092: client order id（DecisionId）伝播（実照会リコンサイルの前提）----

    [Fact]
    public async Task client_order_id対応ブローカにはDecisionIdが伝播される()
    {
        // 滞留 Reserved を後から DecisionId で照合できるよう、発注時に DecisionId をブローカへ紐づける（remark 等）。
        var store = new InMemoryExecutedOrderStore();
        var intent = Intent();
        var broker = new FakeCorrelatingBroker(new BrokerOrder("o1", intent, OrderStatus.Filled, 10, 1_000m, Now, Now));
        var service = NewService(broker, store);
        var approved = Approved(intent);

        await service.ExecuteAsync(approved);

        broker.PropagatedDecisionId.Should().Be(approved.DecisionId, "実照会の突合キーになる DecisionId を伝播する");
        broker.PlainPlaceCount.Should().Be(0, "client order id 対応時は伝播経路のみを使う");
    }

    [Fact]
    public async Task client_order_id非対応ブローカは従来経路で発注される()
    {
        // paper・既存 fake（IClientOrderIdBroker 非実装）は無改修で従来どおり発注できる（capability は任意）。
        var store = new InMemoryExecutedOrderStore();
        var intent = Intent();
        var broker = new FakeBroker(new BrokerOrder("o1", intent, OrderStatus.Filled, 10, 1_000m, Now, Now));
        var service = NewService(broker, store);

        var executed = (await service.ExecuteAsync(Approved(intent))).Executed!;

        executed.Status.Should().Be(OrderStatus.Filled);
        broker.PlaceCount.Should().Be(1);
    }

    [Fact]
    public void 同一DecisionIdの予約は高々1つに限定される()
    {
        // 並行配送（同時に2つのコンシューマが同じ OrderApproved を掴む）の防止。実 PostgreSQL の
        // 一意制約による担保は実基盤 E2E 側で確認する（本テストはポートの契約を固定する）。
        var reservations = new InMemoryOrderReservationStore();
        var decisionId = Guid.NewGuid();

        reservations.TryReserve(decisionId, Now).Should().BeTrue();
        reservations.TryReserve(decisionId, Now).Should().BeFalse();
    }
}
