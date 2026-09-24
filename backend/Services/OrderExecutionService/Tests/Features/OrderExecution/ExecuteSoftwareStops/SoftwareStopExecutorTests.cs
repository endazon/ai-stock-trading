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
        // 市場監視が比べる台帳のラインは保有中のエントリーのうち最も保護的な値（#936, IADR-0393）であり、
        // 別エントリーのラインで到達が出ることがある。この行のライン（930）はまだ割れていない。
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

    // #820 の 5 巡目監査・7 巡目監査, IADR-0344 追記(5)・追記(7): 超過は**その場で観測として記録する**
    //（記録しないと同じ建玉を別の行が主張して売り過ぎる）が、**帳簿（残保護数量）は確定するまで書き換えない**
    //（建玉照会は 1 巡回だけ過少に返り得る）。確定はガードの巡回が行う。
    [Fact]
    public async Task 建玉が既に無ければ決済せず観測を記録して据え置く()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);
        f.Broker.Positions = []; // 手動決済済み

        var result = await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.MarketCloses.Should().BeEmpty("決済を出すと反対建玉（空売り）を作る");
        var saved = f.Stops.Find(stop.EntryDecisionId)!;
        saved.PendingExternalReduction.Should().Be(10, "その巡回で動かせる株数は 0 になる（売り過ぎを作らない）");
        saved.RemainingProtected.Should().Be(10, "帳簿は確定するまで書き換えない（戻す操作を持たないための要）");
        saved.State.Should().Be(ProtectiveStopState.Active, "確定するまで閉じない");
        result.Deferred.Should().Be(1);
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

    // ---- 受け入れ基準 9: 拒否が続く決済（#833 項目2, IADR-0344 追記(14) で改定）----
    // かつては「到達 1 回あたり 3 試行で到達の記録を消して打ち切る」だった。価格が戻ると二度と撃たない（出口を塞ぐ）ため撤去し、
    // 到達の記録を残したまま行ごとの待ち時間を置いて撃ち直しを続ける。待ち時間・Critical の間隔・窓のやり直しは
    // SoftwareStopCloseBackoffTests（T-10-790..T-10-794）が固定する。

    [Fact]
    public async Task 決済が拒否されても到達の記録は消さず待ち時間のあいだは撃ち直さない()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);
        f.Broker.CloseStatus = OrderStatus.Rejected;

        var first = await f.Executor.OnTriggeredAsync(Trigger());
        first.Events.Should().BeEmpty("1 回目の拒否ではまだ Critical を出さない");

        var saved = f.Stops.Find(stop.EntryDecisionId)!;
        saved.State.Should().Be(ProtectiveStopState.Active, "建玉は残っている");
        saved.TriggeredAt.Should().Be(Now, "打ち切りで到達の記録を消さない");
        saved.Attempt.Should().Be(1);
        saved.CloseFailures.Should().Be(1);
        saved.NextCloseAttemptAt.Should().Be(Now.AddSeconds(30));

        // 同じ時刻のガード巡回・続く到達は待ち時間中なので撃たない。
        var retried = await f.Executor.TryCloseAsync(saved, snapshot: null);
        await f.Executor.OnTriggeredAsync(Trigger(detectedAt: Now));
        retried.Kind.Should().Be(SoftwareStopCloseKind.Deferred);
        f.Broker.MarketCloses.Should().ContainSingle();
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

    // T-10-370: #820 の 3 巡目監査（B4・B1 の再来）。ガードが失効した逆指値を再発注すると、行の StopDecisionId は
    // 新しい試行へ進むが、**前試行のレグ記録は Accepted のまま残る**（取り消した注文が当日一覧から消えるブローカーでは
    // 約定追跡が終端化できない）。現在の試行だけを除外していると、その古い記録で S0 の数量を二重に引き、
    // S1 の決済が 1 株も出ないまま黙って据え置かれる。
    [Fact]
    public async Task 再発注済みS0の古いレグ記録があってもS1の持ち分は二重に削られない()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop);
        Entry(f, stop, OrderStatus.Filled, 10);

        // S0 は試行 2 へ再発注済み（行の StopDecisionId は試行 2）。試行 1 の記録が Accepted のまま残っている。
        var s0EntryId = Guid.NewGuid();
        f.Stops.Save(new ProtectiveStopOrder(
            s0EntryId, ProtectiveStopIds.StopDecisionId(s0EntryId, 2), "stop-s0-2", "AAPL", Market.UnitedStates,
            TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 900m, 1m, 2,
            ProtectiveStopState.Active, Now.AddHours(-2), Now.AddHours(-2)));
        foreach (var (attempt, orderId) in new[] { (1, "stop-s0-1"), (2, "stop-s0-2") })
        {
            f.Store.Save(new ExecutionRecord(
                ProtectiveStopIds.StopDecisionId(s0EntryId, attempt), orderId, "AAPL", Market.UnitedStates,
                TradeSide.Sell, ProductType.Cash, PositionEffect.Close, 10, 900m, 0, 0m, OrderStatus.Accepted, 0m,
                Now.AddHours(-2)));
        }

        f.Broker.Positions = [Long(20)]; // S0 の 10 ＋ S1 の 10

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.MarketCloses.Should().ContainSingle()
            .Which.Intent.Quantity.Should().Be(10, "S1 の持ち分は 20 − S0 の 10（古いレグ記録で二度引かない）");
    }

    // T-10-358: #820 の監査（B2）。ガードは 1 巡回に 1 回しか建玉を照会しない。先の行の決済が**即時約定**で返っても、
    // 古い建玉を両方の行へ渡すと売り過ぎる（実測: 保有 15 株に対し 10＋10＝20 株の決済＝反対建玉）。
    // 4 巡目の作り直しでは、**残保護数量が状態であり自分の決済で減って行が完了する**ため、
    // 2 件目の巡回では 1 件目の主張が残っておらず、古い建玉のままでも合計が保有を超えない。
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

        // 1 巡回ぶん: 同じ建玉（15 株）を両方の行へ渡す。手動決済で 5 株減っている状態。
        var snapshot = new[] { Long(15) };
        await f.Executor.TryCloseAsync(f.Stops.Find(first.EntryDecisionId)!, snapshot);
        await f.Executor.TryCloseAsync(f.Stops.Find(second.EntryDecisionId)!, snapshot);

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
        // 4 巡目の作り直し（IADR-0344 追記(4)）では、超過 5 株を**古い行から**一度だけ削る。
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
        f.Broker.MarketCloses.Select(c => c.Intent.Quantity).Should().BeEquivalentTo(new[] { 5, 10 },
            "減った 5 株は古い行（先に建てたエントリー）から削る");
    }

    [Fact]
    public async Task 建玉の減少を観測された行は決済せず確定するまで完了しない()
    {
        // T-10-352: 10 株のエントリー 2 件に対し建玉は 10 株。減った 10 株は**古い行へ**一度だけ観測として記録される。
        // #820 の 5 巡目監査・7 巡目監査, IADR-0344 追記(5)・追記(7):
        // **その巡回で動かせる株数は即座に 0 になるが、帳簿（残保護数量）は確定するまで書き換えない**。
        // 新しい行は 10 株を保持し、決済もその 10 株だけを出す（合計が保有を超えない）。
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
        f.Stops.Find(first.EntryDecisionId)!.PendingExternalReduction.Should().Be(
            10, "その巡回で動かせる株数は 0（だから 1 株も決済しない）");
        f.Stops.Find(first.EntryDecisionId)!.RemainingProtected.Should().Be(
            10, "帳簿は確定するまで書き換えない");
        f.Stops.Find(first.EntryDecisionId)!.State.Should().Be(
            ProtectiveStopState.Active, "観測が確定するまで閉じない");
        f.Stops.Find(second.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed, "10 株を決済し切った");
    }

    [Fact]
    public async Task 持ち分の決定はハンドラとガードで同じで呼ぶ順に依らない()
    {
        // T-10-353: ガード（TryCloseAsync 直接）でも同じ持ち分になる（並行しても合計が保有を超えない）。
        var f = NewFixture();
        var first = SoftwareStop(createdAt: Now.AddHours(-2));
        var second = SoftwareStop(createdAt: Now.AddHours(-1));
        f.Stops.Save(first with { TriggeredAt = Now, TriggeredPrice = 940m });
        f.Stops.Save(second with { TriggeredAt = Now, TriggeredPrice = 940m });
        Entry(f, first, OrderStatus.Filled, 10);
        Entry(f, second, OrderStatus.Filled, 10);
        f.Broker.Positions = [Long(12)];

        // 呼ぶ順に関わらず持ち分は同じ（超過 8 株は古い行から削るので、古い行 2 株・後の行 10 株）。
        // 合計は保有 12 株を超えない。
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

    // ---- #820 の 4 巡目監査（作り直し）: T-10-371・T-10-376・T-10-377 ----

    // T-10-371（受け入れ基準 25）: 残保護数量は「確定 → 自分の決済で減算 → 0 で完了」だけで動く。
    // 建玉の純額から毎回引き直さないことが、4 巡続いた「売り過ぎ／黙って出ない」の振れ幅を無くす鍵である。
    [Fact]
    public async Task 残保護数量は約定確定で決まり決済で減り0で完了する()
    {
        var f = NewFixture();
        var stop = SoftwareStop(quantity: 10);
        f.Stops.Save(stop);
        f.Stops.Find(stop.EntryDecisionId)!.RemainingProtected.Should().BeNull("発注時点では約定が未確定");

        // エントリーは 7 株だけ約定して終端（承認数量 10 ではなく**約定数量**が残保護数量になる）。
        Entry(f, stop, OrderStatus.Cancelled, 7);
        f.Broker.Positions = [Long(7)];

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.MarketCloses.Should().ContainSingle().Which.Intent.Quantity.Should().Be(7);
        var saved = f.Stops.Find(stop.EntryDecisionId)!;
        saved.RemainingProtected.Should().Be(0, "自分の決済ぶんだけ減る");
        saved.State.Should().Be(ProtectiveStopState.Completed, "0 になったときだけ完了する");
    }

    // T-10-376（受け入れ基準 30）: 到達したのに決済できない状態が続くのは正しい fail-safe だが、
    // **無音で続くと「損切りが出ていない」ことに誰も気づかない**。猶予を過ぎたら Critical を 1 回だけ出す。
    [Fact]
    public async Task 到達済みで決済できない状態が猶予を過ぎたらCriticalを一度だけ出す()
    {
        var f = NewFixture();
        var stop = SoftwareStop();
        f.Stops.Save(stop with { TriggeredAt = Now.AddMinutes(-20), TriggeredPrice = 940m });
        Entry(f, stop, OrderStatus.Filled, 10);
        f.Broker.Positions = null; // 建玉を照会できない（据え置きが続く）

        var first = await f.Executor.TryCloseAsync(f.Stops.Find(stop.EntryDecisionId)!, snapshot: null);

        first.Kind.Should().Be(SoftwareStopCloseKind.Deferred, "据え置きは続ける（再試行する）");
        first.Event!.Outcome.Should().Be(SoftwareStopOutcome.CloseStalled);
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
        f.Stops.Find(stop.EntryDecisionId)!.StalledNotifiedAt.Should().Be(Now);

        var second = await f.Executor.TryCloseAsync(f.Stops.Find(stop.EntryDecisionId)!, snapshot: null);
        second.Event.Should().BeNull("毎巡回 Critical を出すと本当に見るべき通知が埋もれる");
    }

    // T-10-377（受け入れ基準 31）: 外部要因の減少は**観測した時点で一度だけ**割り当てて記録する。
    // 次の巡回で引き直さないので、建玉照会の値が変わっても持ち分が揺れない（4 巡の事故の根本原因）。
    // #820 の 7 巡目監査, IADR-0344 追記(7): 記録先は**観測値**（PendingExternalReduction）であり、
    // 帳簿（RemainingProtected）は確定するまで動かない。観測は**増える方向にしか動かない**ので、
    // 照会が一時的に大きく見えても持ち分は戻らない（＝復元という操作が無い）。
    [Fact]
    public async Task 外部要因の減少は一度だけ割り当てて記録し次の巡回で揺れない()
    {
        var f = NewFixture();
        var first = SoftwareStop(createdAt: Now.AddHours(-2));
        var second = SoftwareStop(createdAt: Now.AddHours(-1));
        f.Stops.Save(first);
        f.Stops.Save(second);
        Entry(f, first, OrderStatus.Filled, 10, orderId: "entry-first");
        Entry(f, second, OrderStatus.Filled, 10, orderId: "entry-second");

        // 1 巡目: 20 株のうち 5 株が外部で消えた（建玉 15 株）。超過 5 株は古い行の観測として記録する。
        ProtectiveStopNetting.ReconcileShares(
            "AAPL", Market.UnitedStates, TradeSide.Buy, [Long(15)], f.Stops.FindActive(100), f.Stops, f.Store, Now);
        f.Stops.Find(first.EntryDecisionId)!.PendingExternalReduction.Should().Be(5, "動かせるのは 5 株");
        f.Stops.Find(first.EntryDecisionId)!.RemainingProtected.Should().Be(10, "帳簿は確定するまで動かさない");
        f.Stops.Find(second.EntryDecisionId)!.RemainingProtected.Should().Be(10);

        // 2 巡目: 同じ建玉をもう一度観測しても、同じ減少を二度割り当てない（合計 15 のまま）。
        ProtectiveStopNetting.ReconcileShares(
            "AAPL", Market.UnitedStates, TradeSide.Buy, [Long(15)], f.Stops.FindActive(100), f.Stops, f.Store, Now);
        f.Stops.Find(first.EntryDecisionId)!.PendingExternalReduction.Should().Be(5);
        f.Stops.Find(second.EntryDecisionId)!.PendingExternalReduction.Should().Be(0);

        // 建玉照会が一時的に大きく見えても、観測は戻らない（＝書き戻す＝復元という操作が無い）。
        ProtectiveStopNetting.ReconcileShares(
            "AAPL", Market.UnitedStates, TradeSide.Buy, [Long(20)], f.Stops.FindActive(100), f.Stops, f.Store, Now);
        f.Stops.Find(first.EntryDecisionId)!.PendingExternalReduction.Should().Be(5);
        f.Stops.Find(first.EntryDecisionId)!.RemainingProtected.Should().Be(10);
    }

    // ---- 🔴 T-10-767・T-10-768, FR-10, FR-03, UC-02, ADR-0040 決定1（S1）, #936, IADR-0393 ----
    // 稼働 PoC の AAPL（DB の順）: A＝715 株・ライン 330.88 を**先に**発注し（13:46:45。板に残る指値で、約定は後）、
    // B＝713 株・ライン 331.67 を後に発注した（13:51:48。先に約定）。行の作成時刻は発注の時刻なので A の行が古い。
    // 市場監視は台帳の最も保護的なライン 331.67 で到達を 1 件出す（数量は建玉全体の 1,428 株）。
    // 発注執行は**行ごとに自分のラインで**判定し、達していない行には触らない。二重に売らない仕組みは変えていない。

    private static StopLossTriggered LiveTrigger(decimal price, DateTimeOffset detectedAt) =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 1_428, price, 331.67m, detectedAt);

    private static (ProtectiveStopOrder A715, ProtectiveStopOrder B713) LiveTwoRecordLayout(Fixture f)
    {
        var a715 = SoftwareStop(line: 330.88m, quantity: 715, createdAt: Now.AddHours(-3));
        var b713 = SoftwareStop(line: 331.67m, quantity: 713, createdAt: Now.AddHours(-3).AddMinutes(5));
        f.Stops.Save(a715);
        f.Stops.Save(b713);
        Entry(f, a715, OrderStatus.Filled, 715, orderId: "entry-715");
        Entry(f, b713, OrderStatus.Filled, 713, orderId: "entry-713");
        f.Broker.Positions = [Long(1_428)];
        return (a715, b713);
    }

    [Fact]
    public async Task 稼働中の2行に331_67のラインで到達が出たら713株の行だけを決済し715株の行には触らない()
    {
        // T-10-767: 331.40 は 331.67 には達し 330.88 には達していない。古い行（A）が先に並んでいても A には触らない。
        var f = NewFixture();
        var (a715, b713) = LiveTwoRecordLayout(f);

        var result = await f.Executor.OnTriggeredAsync(LiveTrigger(331.40m, Now));

        result.Matched.Should().Be(1, "自分のラインに達した行は 713 株の行だけ");
        var close = f.Broker.MarketCloses.Should().ContainSingle().Which;
        close.DecisionId.Should().Be(ProtectiveStopIds.SoftwareCloseDecisionId(b713.EntryDecisionId, 1));
        close.Intent.Quantity.Should().Be(713, "到達した行の持ち分だけ。建玉全体（1,428 株）ではない");

        var untouched = f.Stops.Find(a715.EntryDecisionId)!;
        untouched.State.Should().Be(ProtectiveStopState.Active);
        untouched.TriggeredAt.Should().BeNull("330.88 には達していない");
        untouched.Attempt.Should().Be(0);
        f.Stops.Find(b713.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    [Fact]
    public async Task 稼働中の2行は続く到達で715株の行も決済され合計1428株を超えず以後は1株も出さない()
    {
        // T-10-768: 1 本目の決済は受理・未約定のまま（建玉照会は 1,428 株のまま＝未反映）。
        // 2 本目の到達（330.50）で 715 株の行を決済する。未反映の決済を数えずに配分すると 1,428 株を再び主張し得るが、
        // 既存の突き合わせ（残保護数量・送信済みで未反映の決済）がそれを塞ぐ。以後の到達では 1 株も出さない。
        var f = NewFixture();
        var (a715, b713) = LiveTwoRecordLayout(f);

        await f.Executor.OnTriggeredAsync(LiveTrigger(331.40m, Now));
        var second = await f.Executor.OnTriggeredAsync(LiveTrigger(330.50m, Now));
        var third = await f.Executor.OnTriggeredAsync(LiveTrigger(330.00m, Now));

        f.Broker.MarketCloses.Select(c => (c.DecisionId, c.Intent.Quantity)).Should().Equal(
            (ProtectiveStopIds.SoftwareCloseDecisionId(b713.EntryDecisionId, 1), 713),
            (ProtectiveStopIds.SoftwareCloseDecisionId(a715.EntryDecisionId, 1), 715));
        f.Broker.MarketCloses.Sum(c => c.Intent.Quantity).Should().Be(1_428, "建玉を超えて売らない");
        second.Matched.Should().Be(1);
        third.Candidates.Should().Be(0, "両方の行が完了している");
        third.Events.Should().BeEmpty();
    }
}
