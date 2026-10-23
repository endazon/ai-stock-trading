using System.Collections.Concurrent;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
using OrderExecutionService.Infrastructure.Persistence;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// 🔴 T-10-3004〜T-10-3007, NFR, FR-10, FR-05, UC-02, UC-06, ADR-0050 決定1・3, #1446（親 #1423 M-2）, IADR-0575:
// **異なる DecisionId の決済（判断の決済 vs S1 の決済）が同じ建玉へ並行に来ても、合計の決済数量が建玉を超えない。**
//
// 判断の決済は送る前に処理中の決済を差し引き（IADR-0461）、S1 は送る前に処理中の判断の決済を取り消す（IADR-0466）。
// どちらも相手の**記録**を読むが、記録は送った後に書かれる。先発が証券会社へ送っている最中（記録の前）に後発が読むと、
// 後発は先発を見ずに全量を送る。下の偽の証券会社は**未約定の売りが売れる数量を押さえない**（押さえる SIMULATE なら 2 本目は
// 「建玉不足」で拒否されるが、押さえない証券会社では二重に売る＝ADR-0050 決定 3 の実弾の確認事項）。
//
// 窓の作り方: 先発の送信を、後発が証券会社へ届くまで（最大 500 ミリ秒）止める。直列化が効いていれば後発は直列化で待つので届かず、
// 先発は 500 ミリ秒で進む。効いていなければ後発も送り、2 本が並ぶ（T-10-3005 が対照として赤側を固定する）。
public class ClosePositionSerializationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 15, 13, 53, TimeSpan.Zero);

    private const int Held = 1_428;

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => T0;
    }

    private sealed class LiveOrder(string orderId, Guid? decisionId, OrderIntent intent)
    {
        public string OrderId { get; } = orderId;

        public Guid? DecisionId { get; } = decisionId;

        public OrderIntent Intent { get; } = intent;

        public OrderStatus Status { get; set; } = OrderStatus.Accepted;
    }

    // 未約定の売りが売れる数量を押さえない証券会社。注文は約定せず板に残る（取消は効く）。建玉は動かない。
    private sealed class NonReservingBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        private readonly object _gate = new();
        private int _sequence;

        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public ConcurrentDictionary<string, LiveOrder> Orders { get; } = new();

        /// <summary>最初に届いた送信を、2 本目が届くまで（または上限まで）止める。</summary>
        public TaskCompletionSource FirstReachedBroker { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondReachedBroker { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan HoldFirstUpTo { get; init; } = TimeSpan.FromMilliseconds(500);

        /// <summary>板に残っている（取り消されていない）売りの合計。建玉を超えれば二重に売っている。</summary>
        public int OutstandingSells =>
            Orders.Values.Where(o => o.Intent.Side == TradeSide.Sell && o.Status != OrderStatus.Cancelled)
                .Sum(o => o.Intent.Quantity);

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BrokerPositionSnapshot>?>(
                [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, Held, 331m)]);

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            PlaceAsync(intent, decisionId: null);

        public Task<BrokerOrder> PlaceMarketOrderAsync(OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) =>
            PlaceAsync(closeIntent, decisionId);

        // 新規建ての保護逆指値（T-10-3007 の新規建てだけが送る）。受理して返す。板の売りの合計には数えない（決済ではない）。
        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            Task.FromResult(new BrokerOrder($"stop-{decisionId:N}", closeIntent, OrderStatus.Accepted, 0, 0m, T0, null));

        private async Task<BrokerOrder> PlaceAsync(OrderIntent intent, Guid? decisionId)
        {
            bool first;
            lock (_gate)
            {
                first = ++_sequence == 1;
            }

            if (first)
            {
                FirstReachedBroker.TrySetResult();
                await Task.WhenAny(SecondReachedBroker.Task, Task.Delay(HoldFirstUpTo)).ConfigureAwait(false);
            }
            else
            {
                SecondReachedBroker.TrySetResult();
            }

            var id = $"order-{Interlocked.Increment(ref _ids)}";
            Orders[id] = new LiveOrder(id, decisionId, intent);
            return new BrokerOrder(id, intent, OrderStatus.Accepted, 0, 0m, T0, null);
        }

        private int _ids;

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult(Orders.TryGetValue(orderId, out var order)
                ? new BrokerOrder(orderId, order.Intent, order.Status, 0, 0m, T0,
                    OrderStatusLifecycle.IsTerminal(order.Status) ? T0 : null)
                : null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            if (Orders.TryGetValue(orderId, out var order))
                order.Status = OrderStatus.Cancelled;
            return Task.CompletedTask;
        }
    }

    private sealed record Fixture(
        AppSvc Decision, SoftwareStopExecutor S1, NonReservingBroker Broker, InMemoryExecutedOrderStore Store,
        InMemoryProtectiveStopOrderStore Stops, InMemoryOrderReservationStore Reservations);

    // 判断の決済（承認の受け口）と S1 の決済に渡す直列化。serialized=false は是正前（各経路が別々の直列化＝直列にならない）。
    private static Fixture NewFixture(bool serialized)
    {
        var broker = new NonReservingBroker();
        var store = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var clock = new FixedClock();
        IClosePositionLock shared = new InProcessClosePositionLock();
        var decision = new AppSvc(
            broker, store, reservations, clock, stops, logger: null, brokerPositions: broker,
            closeLock: serialized ? shared : new InProcessClosePositionLock());
        var s1 = new SoftwareStopExecutor(
            broker, broker, stops, store, reservations, clock, AlwaysOpenSessionGate.Instance,
            closeLock: serialized ? shared : new InProcessClosePositionLock());

        // PoC と同じ配置: AAPL 1,428 株・S1 の記録 713 株（ライン 331.67）と 715 株（ライン 330.88）。
        SaveS1(stops, store, 713, 331.67m, minutesOld: 120);
        SaveS1(stops, store, 715, 330.88m, minutesOld: 60);
        return new Fixture(decision, s1, broker, store, stops, reservations);
    }

    private static void SaveS1(
        InMemoryProtectiveStopOrderStore stops, InMemoryExecutedOrderStore store, int quantity, decimal line, int minutesOld)
    {
        var id = Guid.NewGuid();
        var at = T0.AddMinutes(-minutesOld);
        stops.Save(new ProtectiveStopOrder(
            id, ProtectiveStopIds.SoftwareStopId(id), string.Empty, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, line, 1m, 0, ProtectiveStopState.Active, at, at,
            StopLossExecutionMethod.SoftwareStop));
        store.Save(new ExecutionRecord(
            id, $"entry-{id:N}", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, quantity, 340m, quantity, 340m, OrderStatus.Filled, 0m, at));
    }

    // 判断の全量の手仕舞い（指値・出どころ TradeDecision）。DecisionId は S1 の決済と異なる。
    private static OrderApproved DecisionClose() =>
        new(Guid.NewGuid(),
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
                Held, 331.60m, PositionEffect.Close),
            Held, T0, Origin: OrderApprovalOrigin.TradeDecision);

    // 713 株の記録（ライン 331.67）だけが到達する価格。
    private static StopLossTriggered Trigger() =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, Held, 331.50m, 331.67m, T0);

    // 先発を証券会社の手前で止めてから後発を走らせ、両方の完了を待つ。
    private static async Task RunRaceAsync(Fixture f, bool decisionFirst)
    {
        Task first = decisionFirst
            ? f.Decision.ExecuteAsync(DecisionClose())
            : f.S1.OnTriggeredAsync(Trigger());
        await f.Broker.FirstReachedBroker.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task second = decisionFirst
            ? Task.Run(() => f.S1.OnTriggeredAsync(Trigger()))
            : Task.Run(() => f.Decision.ExecuteAsync(DecisionClose()));

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
    }

    // 🔴 T-10-3004（本件の中心）: 判断の決済が送信中のところへ S1 が発動する／S1 の決済が送信中のところへ判断の決済が来る。
    // どちらの順でも、板に残る売りの合計は建玉 1,428 株を超えない。
    //   判断が先: S1 は判断の記録を読んでから取り消し（IADR-0466）、713 株を送る（判断の 1,428 株は取り消されて板に残らない）。
    //   S1 が先: 判断は S1 の記録を読んでから差し引き（IADR-0461）、残りの 715 株だけを送る。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T_10_3004_判断の決済とS1の決済が同じ建玉へ並行に来ても合計が建玉を超えない(bool decisionFirst)
    {
        var f = NewFixture(serialized: true);

        await RunRaceAsync(f, decisionFirst);

        f.Broker.OutstandingSells.Should().BeLessThanOrEqualTo(Held, "🔴 同じ建玉を二重に売らない（証券会社が売りを押さえなくても）");
        f.Broker.SecondReachedBroker.Task.IsCompleted.Should().BeTrue("後発も送っている（損切りも手仕舞いも止めない）");
        if (decisionFirst)
        {
            f.Broker.Orders.Values.Should().ContainSingle(o => o.Intent.Quantity == Held)
                .Which.Status.Should().Be(OrderStatus.Cancelled, "S1 は判断の手仕舞いを取り消してから送る（IADR-0466）");
            f.Broker.Orders.Values.Should().ContainSingle(o => o.Status != OrderStatus.Cancelled)
                .Which.Intent.Quantity.Should().Be(713);
        }
        else
        {
            f.Broker.Orders.Values.Select(o => o.Intent.Quantity).Should().BeEquivalentTo([713, 715],
                "判断は処理中の S1 の決済を差し引いた残りだけを送る（IADR-0461）");
        }
    }

    // T-10-3005（対照・是正前の再現）: 直列化を経路ごとに別々にする（＝直列化が無い）と、同じ場面で 2 本が全量を送り、建玉を超える。
    // T-10-3004 の窓が実際に開いていることの証拠（直列化を外すと T-10-3004 と同じ場面が赤になる）。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T_10_3005_直列化が無ければ同じ場面で二重に売る(bool decisionFirst)
    {
        var f = NewFixture(serialized: false);

        await RunRaceAsync(f, decisionFirst);

        f.Broker.OutstandingSells.Should().Be(Held + 713, "後発は先発の記録の前に読み、全量を送る（是正前の挙動）");
    }

    // T-10-3006: プロセス内の直列化は、同じ鍵だけを待たせ、違う鍵（銘柄・市場・決済の方向）は待たせない。待ちの上限で取れなければ例外。
    // 待ちの上限は 1 ミリ秒以上（両実装）。
    // 鍵の 64bit 値は版をまたいで変えない（入れ替えで新旧が並走する間に直列化が割れる）。銘柄の大文字小文字は区別しない。
    [Fact]
    public async Task T_10_3006_同じ鍵だけを待たせ上限で取れなければ例外で鍵は版をまたいで変わらない()
    {
        var gate = new InProcessClosePositionLock(TimeSpan.FromMilliseconds(200));
        var key = new ClosePositionKey("AAPL", Market.UnitedStates, TradeSide.Sell);

        await using (await gate.AcquireAsync(key, CancellationToken.None))
        {
            await using (await gate.AcquireAsync(key with { CloseSide = TradeSide.Buy }, CancellationToken.None)) { }
            await using (await gate.AcquireAsync(key with { Symbol = "MSFT" }, CancellationToken.None)) { }
            await using (await gate.AcquireAsync(key with { Market = Market.Japan }, CancellationToken.None)) { }

            var blocked = () => gate.AcquireAsync(key, CancellationToken.None);
            (await blocked.Should().ThrowAsync<ClosePositionLockUnavailableException>()).Which.Key.Should().Be(key);
        }

        // 外れた後は取れる。
        await using (await gate.AcquireAsync(key, CancellationToken.None)) { }

        key.AdvisoryKey.Should().Be(new ClosePositionKey("aapl", Market.UnitedStates, TradeSide.Sell).AdvisoryKey);
        key.AdvisoryKey.Should().NotBe((key with { CloseSide = TradeSide.Buy }).AdvisoryKey);
        key.AdvisoryKey.Should().Be(ExpectedAaplUsSellKey, "鍵の書式を変えると並走の間に直列化が割れる");

        // 🔴 待ちの上限は 1 ミリ秒以上（0 へ丸めると PostgreSQL の lock_timeout が無期限になる）。両実装とも組み立てで拒否する。
        foreach (var bad in new[] { TimeSpan.Zero, TimeSpan.FromTicks(9_999), TimeSpan.FromMilliseconds(-1), Timeout.InfiniteTimeSpan })
        {
            FluentActions.Invoking(() => new InProcessClosePositionLock(bad)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new PostgresClosePositionLock("Host=localhost", bad)).Should().Throw<ArgumentOutOfRangeException>();
        }

        FluentActions.Invoking(() => new PostgresClosePositionLock("Host=localhost", TimeSpan.FromMilliseconds(1))).Should().NotThrow();
    }

    // AAPL / UnitedStates / Sell の鍵（"AST:close-position:UnitedStates:AAPL:Sell" の SHA-256 の先頭 8 バイト・リトルエンディアン）。
    // 実装と同じ式で計算し直さず、値そのものを固定する（式ごと変えても赤になるように）。
    private const long ExpectedAaplUsSellKey = -159059045747233951;

    private sealed class UnavailableLock : IClosePositionLock
    {
        public int Attempts { get; private set; }

        public Task<IAsyncDisposable> AcquireAsync(ClosePositionKey key, CancellationToken cancellationToken)
        {
            Attempts++;
            throw new ClosePositionLockUnavailableException(key, "試験");
        }
    }

    // T-10-3007（否定形）: 直列化を取れなければ、決済を送らない。
    //   承認の決済: 例外で抜ける（再配送で撃ち直す）。予約は取らず、証券会社にも触れない。
    //   S1: 据え置き（送らない・到達の記録は残る）。
    //   新規建ては直列化を使わない（決済だけを直列にする）。
    [Fact]
    public async Task T_10_3007_直列化を取れなければ決済を送らず新規建ては直列化を使わない()
    {
        var broker = new NonReservingBroker { HoldFirstUpTo = TimeSpan.Zero };
        var store = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var unavailable = new UnavailableLock();
        var decision = new AppSvc(
            broker, store, reservations, new FixedClock(), stops, logger: null, brokerPositions: broker, closeLock: unavailable);
        var s1 = new SoftwareStopExecutor(
            broker, broker, stops, store, reservations, new FixedClock(), AlwaysOpenSessionGate.Instance, closeLock: unavailable);
        SaveS1(stops, store, 713, 331.67m, minutesOld: 120);

        var approved = DecisionClose();
        var act = () => decision.ExecuteAsync(approved);
        await act.Should().ThrowAsync<ClosePositionLockUnavailableException>();
        reservations.Find(approved.DecisionId).Should().BeNull("予約の前に抜ける＝再配送で撃ち直せる");

        var result = await s1.OnTriggeredAsync(Trigger());
        result.Deferred.Should().Be(1);
        stops.FindActiveSoftwareStops("AAPL", Market.UnitedStates, TradeSide.Buy)
            .Should().ContainSingle().Which.TriggeredAt.Should().NotBeNull("到達の記録は残る（次の巡回で撃ち直す）");

        broker.Orders.Should().BeEmpty("🔴 決済は 1 本も送らない");
        unavailable.Attempts.Should().Be(2);

        // 新規建ては直列化を使わない（取れない直列化を渡しても、決済の直列化を問わずに進む）。
        var entry = new OrderApproved(Guid.NewGuid(),
            new OrderIntent("MSFT", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
                10, 400m, PositionEffect.Open, StopLossPrice: 390m),
            10, T0);
        await decision.ExecuteAsync(entry);
        unavailable.Attempts.Should().Be(2, "新規建ては決済の直列化を取りに行かない");
    }
}
