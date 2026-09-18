using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, FR-12, UC-02, ADR-0040 決定1（S1）, #820, IADR-0344 決定4・決定5: ソフトウェア逆指値の発動。
// 受け入れ基準 2〜6・8・9（成行決済・二重決済なし・部分約定・未到達・再起動耐性・手法混在・拒否の打ち切り）を固定する。
public class SoftwareStopExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 15, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FakeBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        /// <summary>建玉（null＝照会不能）。</summary>
        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [Long(10)];

        /// <summary>注文照会の結果（OrderId → 注文。未登録は null）。</summary>
        public Dictionary<string, BrokerOrder> Orders { get; } = new();

        /// <summary>取消したらこの状態・約定数量になる（null＝取消しても状態が変わらない）。</summary>
        public (OrderStatus Status, int Filled)? AfterCancel { get; set; } = (OrderStatus.Cancelled, 0);

        public OrderStatus CloseStatus { get; set; } = OrderStatus.Accepted;
        public Exception? CloseThrows { get; set; }

        public List<(OrderIntent Intent, Guid DecisionId)> MarketCloses { get; } = [];
        public List<string> Cancelled { get; } = [];
        public int PositionQueries { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("発動は通常発注を行わない");

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            throw new NotSupportedException("S1 はブローカーへ逆指値を出さない");

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCloses.Add((closeIntent, decisionId));
            if (CloseThrows is not null)
                throw CloseThrows;
            return Task.FromResult(new BrokerOrder(
                $"close-{MarketCloses.Count}", closeIntent, CloseStatus, 0, 0m, Now,
                OrderStatusLifecycle.IsTerminal(CloseStatus) ? Now : null));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult(Orders.TryGetValue(orderId, out var order) ? order : null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            Cancelled.Add(orderId);
            if (AfterCancel is { } after && Orders.TryGetValue(orderId, out var order))
                Orders[orderId] = order with { Status = after.Status, FilledQuantity = after.Filled };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default)
        {
            PositionQueries++;
            return Task.FromResult(Positions);
        }
    }

    private sealed record Fixture(
        SoftwareStopExecutor Executor, FakeBroker Broker, InMemoryProtectiveStopOrderStore Stops,
        InMemoryExecutedOrderStore Store, InMemoryOrderReservationStore Reservations);

    private static Fixture NewFixture()
    {
        var broker = new FakeBroker();
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        return new Fixture(
            new SoftwareStopExecutor(broker, broker, stops, store, reservations, new FakeClock()),
            broker, stops, store, reservations);
    }

    private static BrokerPositionSnapshot Long(int qty) => new("AAPL", Market.UnitedStates, qty, 1_000m);

    private static ProtectiveStopOrder SoftwareStop(
        Guid? entryDecisionId = null, decimal line = 950m, int quantity = 10, DateTimeOffset? createdAt = null)
    {
        var id = entryDecisionId ?? Guid.NewGuid();
        var at = createdAt ?? Now.AddHours(-1);
        return new ProtectiveStopOrder(
            id, ProtectiveStopIds.SoftwareStopId(id), string.Empty, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, line, 1m, 0, ProtectiveStopState.Active, at, at,
            StopLossExecutionMethod.SoftwareStop);
    }

    // エントリーの発注結果（約定追跡が更新する記録）を置く。
    private static void Entry(
        Fixture f, ProtectiveStopOrder stop, OrderStatus status, int filled, string orderId = "entry-1")
    {
        f.Store.Save(new ExecutionRecord(
            stop.EntryDecisionId, orderId, "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, stop.Quantity, 1_000m, filled, filled > 0 ? 1_000m : 0m, status, 0m, Now.AddHours(-1)));
        f.Broker.Orders[orderId] = new BrokerOrder(
            orderId, new OrderIntent("", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 0, 0m), status, filled, 1_000m, Now.AddHours(-1), null);
    }

    // S0（ブローカー側逆指値）の行と、**本番が必ず書く逆指値レグの発注記録**（PositionEffect=Close・Accepted）を置く。
    // 記録を置かない配置は本番に存在しない（#820 の監査 B1）。
    private static ProtectiveStopOrder BrokerStop(Fixture f, int quantity, decimal line = 900m)
    {
        var entryId = Guid.NewGuid();
        var stopDecisionId = ProtectiveStopIds.StopDecisionId(entryId, 1);
        var row = new ProtectiveStopOrder(
            entryId, stopDecisionId, "stop-s0", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            BrokerProvider.MoomooSimulate, quantity, line, 1m, 1, ProtectiveStopState.Active,
            Now.AddHours(-2), Now.AddHours(-2));
        f.Stops.Save(row);
        f.Store.Save(new ExecutionRecord(
            stopDecisionId, "stop-s0", "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, quantity, line, 0, 0m, OrderStatus.Accepted, 0m, Now.AddHours(-2)));
        return row;
    }

    private static StopLossTriggered Trigger(decimal price = 940m, DateTimeOffset? detectedAt = null) =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 10, price, 950m, detectedAt ?? Now);

    // ---- 受け入れ基準 2: 到達で固定 DecisionId の成行決済 ----

    [Fact]
    public async Task 到達したソフトウェア逆指値は固定のDecisionIdで成行決済され完了する()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);

        var result = await f.Executor.OnTriggeredAsync(Trigger(price: 940m));

        var close = f.Broker.MarketCloses.Should().ContainSingle().Which;
        close.DecisionId.Should().Be(ProtectiveStopIds.SoftwareCloseDecisionId(stop.EntryDecisionId, 1));
        close.Intent.Side.Should().Be(TradeSide.Sell);
        close.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        close.Intent.Quantity.Should().Be(10);
        close.Intent.Price.Should().Be(940m, "参照価格は到達を検知した価格");

        var saved = f.Stops.Find(stop.EntryDecisionId)!;
        saved.State.Should().Be(ProtectiveStopState.Completed);
        saved.Attempt.Should().Be(1);
        saved.TriggeredAt.Should().Be(Now);
        saved.TriggeredPrice.Should().Be(940m);

        // 決済レグは記録され約定追跡に載り、予約は確定している（IADR-0057 / IADR-0113）。
        f.Store.FindByDecisionId(close.DecisionId)!.PositionEffect.Should().Be(PositionEffect.Close);
        f.Reservations.Find(close.DecisionId)!.State.Should().Be(OrderDispatchState.Completed);

        var evt = result.Events.Should().ContainSingle().Which.Should().BeOfType<SoftwareStopExecuted>().Subject;
        evt.Outcome.Should().Be(SoftwareStopOutcome.ClosePlaced);
        evt.CloseDecisionId.Should().Be(close.DecisionId);
        evt.CloseIntent!.Quantity.Should().Be(10);
        evt.StopLossPrice.Should().Be(950m);
        evt.TriggeredPrice.Should().Be(940m);
    }

    // ---- 受け入れ基準 3: 二重決済なし ----

    [Fact]
    public async Task 毎巡回の再発火でも決済は一度だけ発注される()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);

        await f.Executor.OnTriggeredAsync(Trigger(price: 940m));
        var second = await f.Executor.OnTriggeredAsync(Trigger(price: 930m, detectedAt: Now.AddMinutes(1)));
        var redelivered = await f.Executor.OnTriggeredAsync(Trigger(price: 940m));

        f.Broker.MarketCloses.Should().ContainSingle("完了した行は候補に入らない");
        second.Events.Should().BeEmpty();
        redelivered.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task 決済のDecisionIdが予約済みなら発注しない()
    {
        // ハンドラとガードの並行・送信中の再配送: 予約が取れない＝他方が送信中か成否不明。
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);
        f.Reservations.TryReserve(ProtectiveStopIds.SoftwareCloseDecisionId(stop.EntryDecisionId, 1), Now).Should().BeTrue();

        var result = await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.MarketCloses.Should().BeEmpty();
        result.Deferred.Should().Be(1);
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
        f.Stops.Find(stop.EntryDecisionId)!.TriggeredAt.Should().NotBeNull("到達の記録は残す（ガードが再試行する）");
    }

    [Fact]
    public async Task 決済の記録が既にあれば再送せず記録の結果で完了する()
    {
        // 送信・記録の後、行の更新だけが失われた（クラッシュ窓）。
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);
        var closeDecisionId = ProtectiveStopIds.SoftwareCloseDecisionId(stop.EntryDecisionId, 1);
        f.Store.Save(new ExecutionRecord(
            closeDecisionId, "close-prev", "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, 10, 940m, 0, 0m, OrderStatus.Accepted, 0m, Now));

        var result = await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.MarketCloses.Should().BeEmpty();
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        result.Events.OfType<SoftwareStopExecuted>().Should().ContainSingle()
            .Which.CloseOrderId.Should().Be("close-prev");
    }

    // ---- 受け入れ基準 4: 部分約定・未約定 ----

    [Fact]
    public async Task 未約定のエントリーは取り消すだけで決済せずEntryCancelledになる()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Accepted, 0);
        f.Broker.Positions = [];

        var result = await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.Cancelled.Should().ContainSingle().Which.Should().Be("entry-1");
        f.Broker.MarketCloses.Should().BeEmpty();
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        result.Events.OfType<SoftwareStopExecuted>().Should().ContainSingle()
            .Which.Outcome.Should().Be(SoftwareStopOutcome.EntryCancelled);
    }

    [Fact]
    public async Task 部分約定のエントリーは残りを取り消してから約定分だけ決済する()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.PartiallyFilled, 4);
        f.Broker.AfterCancel = (OrderStatus.Cancelled, 4);
        f.Broker.Positions = [Long(4)];

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.Cancelled.Should().ContainSingle();
        f.Broker.MarketCloses.Should().ContainSingle().Which.Intent.Quantity.Should().Be(4);
    }

    [Fact]
    public async Task 取消が終端にならなければ決済を据え置き到達の記録を残す()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.PartiallyFilled, 4);
        f.Broker.AfterCancel = null; // 取消要求を出しても状態が変わらない（非同期の取消）

        var result = await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.MarketCloses.Should().BeEmpty("残りが後から約定すると無保護の建玉が生まれるため、終端まで決済しない");
        result.Deferred.Should().Be(1);
        var saved = f.Stops.Find(stop.EntryDecisionId)!;
        saved.State.Should().Be(ProtectiveStopState.Active);
        saved.TriggeredAt.Should().Be(Now);
    }

    // ---- 受け入れ基準 5: 未到達・到達より後のエントリー ----

    [Fact]
    public async Task 行自身の損切りラインに達していなければ決済しない()
    {
        // 台帳の損切りラインは銘柄単位で最新エントリーの値。別エントリーのライン（930）はまだ割れていない。
        var f = NewFixture();
        var stop = SoftwareStop(line: 930m);
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);

        var result = await f.Executor.OnTriggeredAsync(Trigger(price: 940m));

        result.Matched.Should().Be(0);
        f.Broker.MarketCloses.Should().BeEmpty();
        f.Stops.Find(stop.EntryDecisionId)!.TriggeredAt.Should().BeNull();
    }

    [Fact]
    public async Task 到達の検知より後に建てたエントリーは遅れて届いた到達で決済しない()
    {
        var f = NewFixture();
        var stop = SoftwareStop(createdAt: Now.AddMinutes(5));
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);

        var result = await f.Executor.OnTriggeredAsync(Trigger(detectedAt: Now));

        result.Matched.Should().Be(0);
        f.Broker.MarketCloses.Should().BeEmpty();
    }

    [Fact]
    public async Task 価格が戻っていても一度到達した到達は決済する()
    {
        // FR-10: 損切りラインは到達で発動する。検知時の価格で判定し、処理時点の価格は見ない。
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);

        await f.Executor.OnTriggeredAsync(Trigger(price: 949m, detectedAt: Now.AddHours(-0.5)));

        f.Broker.MarketCloses.Should().ContainSingle();
    }

    [Fact]
    public async Task 建玉が既に無ければ決済せず完了する()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);
        f.Broker.Positions = []; // 手動決済済み

        var result = await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.MarketCloses.Should().BeEmpty("決済を出すと反対建玉（空売り）を作る");
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        result.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task 建玉が一部だけ残っていれば残りだけを決済する()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);
        f.Broker.Positions = [Long(3)];

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.MarketCloses.Should().ContainSingle().Which.Intent.Quantity.Should().Be(3);
    }

    // ---- 受け入れ基準 6: 据え置き（再起動耐性） ----

    [Fact]
    public async Task 接続断では予約を解放し到達を記録して据え置く()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);
        f.Broker.CloseThrows = new BrokerUnavailableException("OpenD へ接続できません（テスト）");

        var result = await f.Executor.OnTriggeredAsync(Trigger());

        result.Deferred.Should().Be(1);
        result.Events.Should().BeEmpty();
        var saved = f.Stops.Find(stop.EntryDecisionId)!;
        saved.State.Should().Be(ProtectiveStopState.Active);
        saved.TriggeredAt.Should().Be(Now);
        saved.Attempt.Should().Be(0, "送っていないので試行は進めない");
        f.Reservations.Find(ProtectiveStopIds.SoftwareCloseDecisionId(stop.EntryDecisionId, 1))
            .Should().BeNull("確実に未発注のため予約を解放する（次回同じ DecisionId で送れる）");

        // 復旧後の再試行（ガードの巡回と同じ入口）で決済される。
        f.Broker.CloseThrows = null;
        var retry = await f.Executor.TryCloseAsync(f.Stops.Find(stop.EntryDecisionId)!, snapshot: null);
        retry.Kind.Should().Be(SoftwareStopCloseKind.Completed);
        f.Broker.MarketCloses.Should().HaveCount(2);
        f.Broker.MarketCloses[1].DecisionId.Should().Be(ProtectiveStopIds.SoftwareCloseDecisionId(stop.EntryDecisionId, 1));
    }

    [Fact]
    public async Task 送信結果が不明な例外では予約を残し同じDecisionIdで再送しない()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);
        f.Broker.CloseThrows = new InvalidOperationException("送信後に切断（テスト）");

        await f.Executor.OnTriggeredAsync(Trigger());
        f.Broker.CloseThrows = null;
        var retry = await f.Executor.TryCloseAsync(f.Stops.Find(stop.EntryDecisionId)!, snapshot: null);

        retry.Kind.Should().Be(SoftwareStopCloseKind.Deferred);
        f.Broker.MarketCloses.Should().ContainSingle("届いたか不明な注文に重ねて送らない");
    }

    [Fact]
    public async Task 建玉を照会できなければ据え置く()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);
        f.Broker.Positions = null;

        var result = await f.Executor.OnTriggeredAsync(Trigger());

        result.Deferred.Should().Be(1);
        f.Broker.MarketCloses.Should().BeEmpty("不明を「建玉なし」とも「建玉あり」とも取り違えない");
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    // ---- 受け入れ基準 9: 拒否の打ち切り ----

    [Fact]
    public async Task 決済が拒否され続けたら到達1回あたり3試行で打ち切りCriticalを出す()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);
        f.Broker.CloseStatus = OrderStatus.Rejected;

        var first = await f.Executor.OnTriggeredAsync(Trigger());
        first.Events.Should().BeEmpty("1 回目の拒否ではまだ打ち切らない");
        f.Stops.Find(stop.EntryDecisionId)!.TriggeredAt.Should().NotBeNull();

        await f.Executor.TryCloseAsync(f.Stops.Find(stop.EntryDecisionId)!, snapshot: null);
        var third = await f.Executor.TryCloseAsync(f.Stops.Find(stop.EntryDecisionId)!, snapshot: null);

        f.Broker.MarketCloses.Select(c => c.DecisionId).Should().OnlyHaveUniqueItems("試行ごとに別の DecisionId");
        f.Broker.MarketCloses.Should().HaveCount(3);
        third.Event!.Outcome.Should().Be(SoftwareStopOutcome.CloseRejected);
        third.Event.Attempt.Should().Be(3);
        var saved = f.Stops.Find(stop.EntryDecisionId)!;
        saved.State.Should().Be(ProtectiveStopState.Active, "建玉は残っている");
        saved.TriggeredAt.Should().BeNull("次の到達まで再試行しない");

        // 次の到達で再開する（試行番号は続きから）。
        f.Broker.CloseStatus = OrderStatus.Accepted;
        await f.Executor.OnTriggeredAsync(Trigger(detectedAt: Now.AddMinutes(1)));
        f.Broker.MarketCloses.Should().HaveCount(4);
        f.Broker.MarketCloses[3].DecisionId.Should().Be(ProtectiveStopIds.SoftwareCloseDecisionId(stop.EntryDecisionId, 4));
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // ---- 受け入れ基準 8: 手法混在 ----

    [Fact]
    public async Task S0の建玉が同じ銘柄にあってもS1の決済はS0の数量を食わない()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);
        BrokerStop(f, quantity: 5);
        f.Broker.Positions = [Long(12)]; // S1 の 10 のうち 3 を手動決済済み、S0 の 5 は保持

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.MarketCloses.Should().ContainSingle().Which.Intent.Quantity.Should().Be(7, "純額 12 − S0 の 5");
    }

    // T-10-357: #820 の監査（B1）。S0 の逆指値レグは PositionEffect=Close の記録としても残る。
    // 行の数量と記録の両方で差し引くと二重に削られ、S0 と S1 が同数なら決済が 1 株も出ない
    //（損切りが黙って効かなくなる。Critical も出ない）。
    [Fact]
    public async Task S0とS1が同数でもS1の決済は出る_S0のレグ記録で二重に差し引かない()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);
        BrokerStop(f, quantity: 10);
        f.Broker.Positions = [Long(20)]; // S0 の 10 ＋ S1 の 10

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.MarketCloses.Should().ContainSingle()
            .Which.Intent.Quantity.Should().Be(10, "S1 の持ち分は 20 − S0 の 10");
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // T-10-358: #820 の監査（B2）。ガードは 1 巡回に 1 回しか建玉を照会しない。先の行の決済が**即時約定**で返ると、
    // その約定は（古い）建玉にも未約定一覧にも現れず、後の行が同じ建玉を再配分されて売り過ぎる
    //（実測: 保有 15 株に対し 10＋10＝20 株の決済＝反対建玉）。照会時刻より後に動いた決済は未反映として差し引く。
    [Fact]
    public async Task ガード巡回で先の行の決済が即時約定しても合計が建玉を超えない()
    {
        var f = NewFixture();
        var first = SoftwareStop(quantity: 10, createdAt: Now.AddHours(-3));
        var second = SoftwareStop(quantity: 10, createdAt: Now.AddHours(-2));
        f.Stops.Save(first with { TriggeredAt = Now, TriggeredPrice = 940m });
        f.Stops.Save(second with { TriggeredAt = Now, TriggeredPrice = 940m });
        Entry(f, first, OrderStatus.Filled, 10, orderId: "entry-first");
        Entry(f, second, OrderStatus.Filled, 10, orderId: "entry-second");

        // 決済は即座に約定して返る（moomoo が FilledAll を返す経路。MapState が Filled へ写す）。
        f.Broker.CloseStatus = OrderStatus.Filled;

        // 1 巡回ぶん: 同じ建玉（15 株）・同じ照会時刻を両方の行へ渡す。手動決済で 5 株減っている状態。
        var snapshot = new[] { Long(15) };
        await f.Executor.TryCloseAsync(f.Stops.Find(first.EntryDecisionId)!, snapshot, Now);
        await f.Executor.TryCloseAsync(f.Stops.Find(second.EntryDecisionId)!, snapshot, Now);

        f.Broker.MarketCloses.Sum(c => c.Intent.Quantity)
            .Should().Be(15, "巡回の合計が保有数量を超えない（超えると反対建玉＝空売りになる）");
    }

    // T-10-359: 差し引きは「照会がまだ映していない決済」に限る。**建玉へ反映済みの古い決済まで差し引くと**、
    // 持ち分が永久に足りず S1 が損切りできなくなる（B2 の是正が逆側へ倒れていないことを固定する）。
    [Fact]
    public async Task 建玉へ反映済みの古い決済は差し引かない()
    {
        var f = NewFixture();
        var stop = SoftwareStop(quantity: 10);
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);
        // 30 分前に約定した別の決済（建玉 10 株は既にこの約定を織り込んだ後の値）。
        f.Store.Save(new ExecutionRecord(
            Guid.NewGuid(), "close-old", "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, 5, 1_000m, 5, 1_000m, OrderStatus.Filled, 0m, Now.AddMinutes(-30)));
        f.Broker.Positions = [Long(10)];

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.MarketCloses.Should().ContainSingle()
            .Which.Intent.Quantity.Should().Be(10, "反映済みの決済は持ち分を削らない");
    }

    [Fact]
    public async Task 別銘柄や別方向や完了済みのソフトウェア逆指値は対象にならない()
    {
        var f = NewFixture();
        var other = SoftwareStop() with { Symbol = "MSFT" };
        var done = SoftwareStop() with { State = ProtectiveStopState.Completed };
        f.Stops.Save(other);
        f.Stops.Save(done);

        var result = await f.Executor.OnTriggeredAsync(Trigger());

        result.Candidates.Should().Be(0);
        f.Broker.MarketCloses.Should().BeEmpty();
    }

    // ---- #820 の監査（売り過ぎの防止）: T-10-351〜353 ----

    [Fact]
    public async Task 同じ銘柄の複数のソフトウェア逆指値は建玉を配分し合計で保有数量を超えない()
    {
        // T-10-351: 10 株のエントリー 2 件（計 20 株）のうち 5 株が手動決済され、建玉は 15 株。
        // 行ごとに「建玉残」を上限にすると 10+10=20 株を売って空売りになる（監査の実測）。
        var f = NewFixture();
        var first = SoftwareStop(createdAt: Now.AddHours(-2));
        var second = SoftwareStop(createdAt: Now.AddHours(-1));
        f.Stops.Save(first);
        f.Stops.Save(second);
        Entry(f, first, OrderStatus.Filled, 10);
        Entry(f, second, OrderStatus.Filled, 10);
        f.Broker.Positions = [Long(15)];

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.MarketCloses.Sum(c => c.Intent.Quantity).Should().Be(15, "合計が保有数量を超えない");
        f.Broker.MarketCloses.Select(c => c.Intent.Quantity).Should().BeEquivalentTo(new[] { 10, 5 },
            "古い行（先に建てたエントリー）へ先に配分する");
    }

    [Fact]
    public async Task 持ち分が無い行は建玉が残っていても完了させず据え置く()
    {
        // T-10-352: 先の行へ全量を配分した後の行。完了させると保護のない建玉が残るため据え置く。
        var f = NewFixture();
        var first = SoftwareStop(createdAt: Now.AddHours(-2));
        var second = SoftwareStop(createdAt: Now.AddHours(-1));
        f.Stops.Save(first);
        f.Stops.Save(second);
        Entry(f, first, OrderStatus.Filled, 10);
        Entry(f, second, OrderStatus.Filled, 10);
        f.Broker.Positions = [Long(10)];

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.MarketCloses.Should().HaveCount(1);
        f.Broker.MarketCloses[0].Intent.Quantity.Should().Be(10);
        f.Stops.Find(second.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
        f.Stops.Find(second.EntryDecisionId)!.TriggeredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task 配分は到達時刻と作成時刻で決まりハンドラとガードで同じになる()
    {
        // T-10-353: ガード（TryCloseAsync 直接）でも同じ配分になる（並行しても合計が保有を超えない）。
        var f = NewFixture();
        var first = SoftwareStop(createdAt: Now.AddHours(-2));
        var second = SoftwareStop(createdAt: Now.AddHours(-1));
        f.Stops.Save(first with { TriggeredAt = Now, TriggeredPrice = 940m });
        f.Stops.Save(second with { TriggeredAt = Now, TriggeredPrice = 940m });
        Entry(f, first, OrderStatus.Filled, 10);
        Entry(f, second, OrderStatus.Filled, 10);
        f.Broker.Positions = [Long(12)];

        // 呼ぶ順に関わらず配分は同じ（古い行へ 10 株、後の行へ残り 2 株）。合計は保有 12 株を超えない。
        var later = await f.Executor.TryCloseAsync(f.Stops.Find(second.EntryDecisionId)!, snapshot: null);
        var earlier = await f.Executor.TryCloseAsync(f.Stops.Find(first.EntryDecisionId)!, snapshot: null);

        later.Kind.Should().Be(SoftwareStopCloseKind.Completed);
        earlier.Kind.Should().Be(SoftwareStopCloseKind.Completed);
        f.Broker.MarketCloses.Select(c => c.Intent.Quantity).Should().BeEquivalentTo(new[] { 2, 10 });
        f.Broker.MarketCloses.Sum(c => c.Intent.Quantity).Should().Be(12);
    }

    // ---- #820 の監査（孤立行）: T-10-354〜356 ----

    [Fact]
    public async Task エントリーの発注記録が無い行は決済せず猶予内は据え置く()
    {
        // T-10-354: 記録の数量で決済すると同じ銘柄の別の建玉を売る（監査の実測）。1 株も出さない。
        var f = NewFixture();
        var stop = SoftwareStop(createdAt: Now.AddMinutes(-5));
        f.Stops.Save(stop);
        f.Broker.Positions = [Long(10)];

        var result = await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.MarketCloses.Should().BeEmpty();
        result.Deferred.Should().Be(1);
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    [Fact]
    public async Task エントリーの発注記録が無い行は猶予を過ぎるとEntryMissingで閉じる()
    {
        // T-10-355: 閉じないと毎巡回この行で決済を試み続ける。閉じる前に Critical で人手へ知らせる。
        var f = NewFixture();
        var stop = SoftwareStop(createdAt: Now.AddMinutes(-20));
        f.Stops.Save(stop);
        f.Broker.Positions = [Long(10)];

        var result = await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.MarketCloses.Should().BeEmpty();
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        result.Events.Should().ContainSingle()
            .Which.Should().BeOfType<SoftwareStopExecuted>()
            .Which.Outcome.Should().Be(SoftwareStopOutcome.EntryMissing);
    }

    [Fact]
    public async Task 孤立行の決済は他の行の建玉を食わない()
    {
        // T-10-356: 孤立行（記録なし・10 株）と実在の 10 株。監査の実測では 20 株の決済になっていた。
        var f = NewFixture();
        var orphan = SoftwareStop(createdAt: Now.AddMinutes(-20));
        var real = SoftwareStop(createdAt: Now.AddMinutes(-10));
        f.Stops.Save(orphan);
        f.Stops.Save(real);
        Entry(f, real, OrderStatus.Filled, 10);
        f.Broker.Positions = [Long(10)];

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.MarketCloses.Sum(c => c.Intent.Quantity).Should().Be(10);
        f.Stops.Find(orphan.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }
}
