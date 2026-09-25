using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Hosted;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 FR-10, UC-02, #1013, IADR-0428（2026-09-26 追記）, IADR-0210（2026-09-26 追記）:
// 保護逆指値ガードは「ブローカーの建玉 0」を建玉消滅と読む前に**エントリー注文の状態**を確かめる（原則 A）。
//
// 指値のエントリーが受理済み・未約定のあいだ建玉は 0 である。是正前のガードはこの 0 を「建玉が消えた」と読み、
// 生きている逆指値を取り消して記録を Completed にした——エントリーが後で約定しても次の巡回は何もしない（無保護の建玉）。
// 失効した逆指値の行・#853 の送信結果待ち（予約が落ちた・解放された）の行も、建玉 0 なら記録を閉じていた。
//
// 固定する表（作業仕様書 20260926_1013_guard-entry-state-before-position-gone）:
//   非終端（受理・部分約定）→ 取り消さない・閉じない／終端・約定 0 → 従来どおり（取消・完了）／
//   約定あり → 建玉を照会し直して 0 のときだけ従来どおり／不明 → 据え置き＋ EntryStateUnknown（1 時間ごと）
//
// 殺す変異（手で入れて赤を確かめた）: 非終端を「消えた」へ倒す（T-10-1120 / 1121 / 1127 / 1128）／終端・約定 0 を据え置きへ倒す（T-10-1122）／
// 約定済みの照会し直しを外す（T-10-1125）／照会し直しの null を「消えた」へ倒す（T-10-1126）／不明を「約定済み」へ倒す（T-10-1124）／
// 通知の間隔を外す（T-10-1124）／終端の記録でもブローカーへ照会する（T-10-1129）／常駐の補償から新しい値を外す（T-10-1130）。
public class ProtectiveStopGuardEntryStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 14, 0, 0, TimeSpan.Zero);
    private const string EntryOrderId = "entry-1";
    private const string StopOrderId = "stop-1";

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class EntryStateBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        /// <summary>注文 ID → 照会結果（未登録は null＝照会できない）。</summary>
        public Dictionary<string, BrokerOrder?> Orders { get; } = new();

        public HashSet<string> ThrowOnGetOrderIds { get; } = [];

        /// <summary>建玉照会の結果。<see cref="PositionSequence"/> が空になったらこの値を返す（null＝照会不能）。</summary>
        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [];

        /// <summary>呼ばれた順に返す建玉照会の結果（巡回の最初の照会と、約定を知った後の照会し直しを分けて注入する）。</summary>
        public Queue<IReadOnlyList<BrokerPositionSnapshot>?> PositionSequence { get; } = new();

        public List<string> Cancelled { get; } = [];
        public List<string> OrderQueries { get; } = [];
        public int PositionCalls { get; private set; }
        public int StopPlaceCount { get; private set; }
        public int MarketCloseCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("ガードは通常発注を行わない");

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default)
        {
            StopPlaceCount++;
            return Task.FromResult(new BrokerOrder(
                $"stop-re-{StopPlaceCount}", closeIntent, OrderStatus.Accepted, 0, 0m, Now, null));
        }

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCloseCount++;
            return Task.FromResult(new BrokerOrder(
                $"close-{MarketCloseCount}", closeIntent, OrderStatus.Filled, closeIntent.Quantity, closeIntent.Price, Now, Now));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default)
        {
            OrderQueries.Add(orderId);
            if (ThrowOnGetOrderIds.Contains(orderId))
                throw new InvalidOperationException($"注文照会に失敗（テスト）: {orderId}");
            return Task.FromResult(Orders.TryGetValue(orderId, out var order) ? order : null);
        }

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            Cancelled.Add(orderId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default)
        {
            PositionCalls++;
            return Task.FromResult(PositionSequence.Count > 0 ? PositionSequence.Dequeue() : Positions);
        }
    }

    private sealed record Harness(
        ProtectiveStopGuard Guard,
        EntryStateBroker Broker,
        InMemoryProtectiveStopOrderStore Stops,
        InMemoryExecutedOrderStore Store,
        InMemoryOrderReservationStore Reservations,
        MutableClock Clock,
        HeldCloseNotificationTracker Held,
        Guid EntryDecisionId)
    {
        public ProtectiveStopOrder Row => Stops.Find(EntryDecisionId)!;

        public Task<ProtectiveStopGuardResult> PatrolAsync() => Guard.RunOnceAsync(batchSize: 10);
    }

    // S0 の行（逆指値 stop-1・試行 1）とエントリーの発注記録（DecisionId＝EntryDecisionId）を置く。
    private static Harness NewHarness(
        OrderStatus? entryRecordStatus = OrderStatus.Accepted, int entryRecordFilled = 0, ProtectiveStopOrder? row = null)
    {
        var entry = row?.EntryDecisionId ?? Guid.NewGuid();
        var broker = new EntryStateBroker();
        var stops = new InMemoryProtectiveStopOrderStore();
        stops.Save(row ?? LiveRow(entry));
        var store = new InMemoryExecutedOrderStore();
        if (entryRecordStatus is { } status)
        {
            store.Save(new ExecutionRecord(
                entry, EntryOrderId, "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, PositionEffect.Open,
                10, 1_000m, entryRecordFilled, entryRecordFilled > 0 ? 1_000m : 0m, status, 0m, Now.AddMinutes(-10)));
        }

        var reservations = new InMemoryOrderReservationStore();
        var clock = new MutableClock(Now);
        var held = new HeldCloseNotificationTracker();
        var guard = new ProtectiveStopGuard(
            broker, broker, stops, store, reservations, clock,
            heldCloseNotifications: held, closeRejections: new CloseRejectionTracker());
        return new Harness(guard, broker, stops, store, reservations, clock, held, entry);
    }

    private static ProtectiveStopOrder LiveRow(Guid entry) =>
        new(entry, ProtectiveStopIds.StopDecisionId(entry, 1), StopOrderId, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, Attempt: 1, ProtectiveStopState.Active,
            Now.AddMinutes(-10), Now.AddMinutes(-10));

    // #853 の経路: 送信結果待ち（注文 ID が空）で、逆指値レグの予約も発注結果の記録も無い（予約が例外で落ちた・解放された）。
    private static ProtectiveStopOrder PendingRowWithoutReservation(Guid entry) =>
        new(entry, ProtectiveStopIds.StopDecisionId(entry, 1), StopOrderId: string.Empty, "AAPL", Market.UnitedStates,
            TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, Attempt: 1,
            ProtectiveStopState.Active, Now.AddMinutes(-10), Now.AddMinutes(-10), RemainingProtected: 10);

    private static BrokerOrder StopOrder(OrderStatus status) =>
        new(StopOrderId, new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 10, 950m, PositionEffect.Close), status, 0, 0m, Now,
            OrderStatusLifecycle.IsTerminal(status) ? Now : null);

    private static BrokerOrder EntryOrder(OrderStatus status, int filled) =>
        new(EntryOrderId, new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 10, 1_000m, PositionEffect.Open), status, filled, filled > 0 ? 1_000m : 0m, Now,
            OrderStatusLifecycle.IsTerminal(status) ? Now : null);

    private static BrokerPositionSnapshot Long(int qty) => new("AAPL", Market.UnitedStates, qty, 1_000m);

    private static IEnumerable<ProtectiveStopCoverageLost> UnknownNotices(ProtectiveStopGuardResult result) =>
        result.Events.OfType<ProtectiveStopCoverageLost>()
            .Where(e => e.Remediation == ProtectiveStopRemediation.EntryStateUnknown);

    // ---- 🔴 T-10-1120: 監査の再現（生きている逆指値・未約定のエントリー・建玉 0 → 取り消さない。約定後も保護は残る） ----

    [Fact]
    public async Task 未約定のエントリーで建玉が0でも生きている逆指値を取り消さず_約定後も保護が残る()
    {
        var h = NewHarness();
        h.Broker.Orders[StopOrderId] = StopOrder(OrderStatus.Accepted);
        h.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.Accepted, filled: 0);
        h.Broker.Positions = []; // 指値のエントリーがまだ約定していない

        var first = await h.PatrolAsync();

        h.Broker.Cancelled.Should().BeEmpty("取り消すと、エントリーの約定後に逆指値の無い建玉が残る（#1013 の実測）");
        h.Row.State.Should().Be(ProtectiveStopState.Active, "閉じると約定後の巡回が何もしない");
        first.StillActive.Should().Be(1);
        first.Completed.Should().Be(0);
        UnknownNotices(first).Should().BeEmpty("状態は分かっている（未約定）。人を呼ぶ理由は無い");

        // エントリーが約定して建玉が現れた（約定追跡が発注記録へ反映する前でもよい）。
        h.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.Filled, filled: 10);
        h.Broker.Positions = [Long(10)];

        var second = await h.PatrolAsync();

        h.Broker.Cancelled.Should().BeEmpty("同じ逆指値が建玉を守っている");
        h.Broker.StopPlaceCount.Should().Be(0, "生きている逆指値に重ねて張らない");
        h.Row.State.Should().Be(ProtectiveStopState.Active);
        h.Row.StopOrderId.Should().Be(StopOrderId);
        second.StillActive.Should().Be(1);
    }

    // ---- T-10-1121: 部分約定（非終端）・建玉 0 → 取り消さない ----

    [Fact]
    public async Task 部分約定のまま生きているエントリーで建玉が0なら取り消さない_否定形()
    {
        var h = NewHarness();
        h.Broker.Orders[StopOrderId] = StopOrder(OrderStatus.Accepted);
        h.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.PartiallyFilled, filled: 4);
        h.Broker.Positions = [];

        var result = await h.PatrolAsync();

        h.Broker.Cancelled.Should().BeEmpty("エントリーは生きていて、残りもこれから約定し得る");
        h.Row.State.Should().Be(ProtectiveStopState.Active);
        result.StillActive.Should().Be(1);
    }

    // ---- T-10-1122: 終端・約定 0（取消・失効・拒否）→ 従来どおり取り消して完了 ----

    [Theory]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    [InlineData(OrderStatus.Rejected)]
    public async Task 約定しないまま終わったエントリーなら生きている逆指値を取り消して完了する(OrderStatus terminal)
    {
        var h = NewHarness();
        h.Broker.Orders[StopOrderId] = StopOrder(OrderStatus.Accepted);
        h.Broker.Orders[EntryOrderId] = EntryOrder(terminal, filled: 0);
        h.Broker.Positions = [];

        var result = await h.PatrolAsync();

        h.Broker.Cancelled.Should().Equal([StopOrderId], "建玉は生じなかった——残る逆指値は発火すると反対方向の建玉を生む");
        h.Row.State.Should().Be(ProtectiveStopState.Completed);
        result.Completed.Should().Be(1);
        h.Broker.PositionCalls.Should().Be(1, "約定 0 なら建玉を照会し直す理由が無い");
    }

    // ---- T-10-1123: 約定済み・照会し直しても建玉 0 → 従来どおり（建って消えた） ----

    [Fact]
    public async Task 約定済みのエントリーで照会し直しても建玉が0なら取り消して完了する()
    {
        var h = NewHarness();
        h.Broker.Orders[StopOrderId] = StopOrder(OrderStatus.Accepted);
        h.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.Filled, filled: 10);
        h.Broker.Positions = []; // owner 手仕舞い・強制決済などで消えた

        var result = await h.PatrolAsync();

        h.Broker.Cancelled.Should().Equal([StopOrderId]);
        h.Row.State.Should().Be(ProtectiveStopState.Completed);
        result.Completed.Should().Be(1);
        h.Broker.PositionCalls.Should().Be(2, "約定を知った後にもう一度照会してから「消えた」と判定する");
    }

    // ---- 🔴 T-10-1124: エントリーの状態が不明 → 据え置き・通知は 1 時間ごと ----

    public enum UnknownCause
    {
        QueryReturnsNull,
        QueryThrows,
        NoEntryRecord,
    }

    [Theory]
    [InlineData(UnknownCause.QueryReturnsNull)]
    [InlineData(UnknownCause.QueryThrows)]
    [InlineData(UnknownCause.NoEntryRecord)]
    public async Task エントリーの状態が分からなければ取り消さず閉じず_通知は1時間ごと_否定形(UnknownCause cause)
    {
        var h = NewHarness(entryRecordStatus: cause == UnknownCause.NoEntryRecord ? null : OrderStatus.Accepted);
        h.Broker.Orders[StopOrderId] = StopOrder(OrderStatus.Accepted);
        if (cause == UnknownCause.QueryThrows)
            h.Broker.ThrowOnGetOrderIds.Add(EntryOrderId);
        // QueryReturnsNull: エントリー注文は未登録（照会が null）。
        h.Broker.Positions = [];

        var first = await h.PatrolAsync();

        h.Broker.Cancelled.Should().BeEmpty("分からないを「建玉が消えた」と取り違えない（原則 A）");
        h.Row.State.Should().Be(ProtectiveStopState.Active, "閉じると、未約定だった場合に約定後の建玉が無保護で残る");
        first.Unknown.Should().Be(1);
        first.Failed.Should().Be(0, "照会の例外で巡回を落とさず、不明として据え置く");
        var notice = UnknownNotices(first).Should().ContainSingle().Which;
        notice.EntryDecisionId.Should().Be(h.EntryDecisionId);
        notice.Quantity.Should().Be(10);
        notice.Cause.Should().Be(ProtectiveStopLossCause.LapsedInFlight);
        notice.CloseDecisionId.Should().BeNull("何も送っていない");
        notice.CloseIntent.Should().BeNull("台帳に処理中の決済を押さえさせない");

        // 30 秒後の巡回は通知を重ねない（ログだけ）。
        h.Clock.UtcNow = Now.AddSeconds(30);
        var second = await h.PatrolAsync();
        UnknownNotices(second).Should().BeEmpty("30 秒ごとに鳴らすと通知が埋もれる");
        second.Unknown.Should().Be(1);

        // 1 時間たったら出し直す（通知を 1 回見逃しても無期限に黙らない）。
        h.Clock.UtcNow = Now.AddHours(1);
        var third = await h.PatrolAsync();
        UnknownNotices(third).Should().ContainSingle();
        h.Broker.Cancelled.Should().BeEmpty();
        h.Row.State.Should().Be(ProtectiveStopState.Active);
    }

    // ---- 🔴 T-10-1125: 巡回の建玉照会より後にエントリーが約定した → 照会し直して建玉があれば取り消さない ----

    [Fact]
    public async Task 巡回の照会の後に約定したエントリーは照会し直した建玉で判定し_取り消さない_否定形()
    {
        var h = NewHarness();
        h.Broker.Orders[StopOrderId] = StopOrder(OrderStatus.Accepted);
        h.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.Filled, filled: 10);
        h.Broker.PositionSequence.Enqueue([]);           // 巡回の最初の照会（約定の直前）
        h.Broker.PositionSequence.Enqueue([Long(10)]);   // 約定を知った後の照会
        h.Broker.Positions = [Long(10)];

        var result = await h.PatrolAsync();

        h.Broker.Cancelled.Should().BeEmpty("約定の直前に取った 0 で、約定した建玉の逆指値を取り消さない");
        h.Row.State.Should().Be(ProtectiveStopState.Active);
        result.StillActive.Should().Be(1);
    }

    // ---- T-10-1126: 照会し直しが不明 → 据え置き ----

    [Fact]
    public async Task 約定済みでも照会し直しが不明なら据え置く_否定形()
    {
        var h = NewHarness();
        h.Broker.Orders[StopOrderId] = StopOrder(OrderStatus.Accepted);
        h.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.Filled, filled: 10);
        h.Broker.PositionSequence.Enqueue([]);
        h.Broker.PositionSequence.Enqueue(null);

        var result = await h.PatrolAsync();

        h.Broker.Cancelled.Should().BeEmpty();
        h.Row.State.Should().Be(ProtectiveStopState.Active);
        result.Unknown.Should().Be(1);
    }

    // ---- T-10-1127: 逆指値が失効・エントリー未約定・建玉 0 → 閉じない。約定後の巡回で張り直す ----

    [Fact]
    public async Task 逆指値が失効してもエントリーが未約定なら記録を閉じず_約定後の巡回で張り直す()
    {
        var h = NewHarness();
        h.Broker.Orders[StopOrderId] = StopOrder(OrderStatus.Cancelled);
        h.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.Accepted, filled: 0);
        h.Broker.Positions = [];

        var first = await h.PatrolAsync();

        h.Row.State.Should().Be(ProtectiveStopState.Active, "閉じると約定後の建玉を誰も守らない");
        first.Completed.Should().Be(0);
        h.Broker.StopPlaceCount.Should().Be(0, "建玉がまだ無い——張ると建玉なき逆指値になる");
        h.Broker.MarketCloseCount.Should().Be(0);

        h.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.Filled, filled: 10);
        h.Broker.Positions = [Long(10)];

        var second = await h.PatrolAsync();

        second.Replaced.Should().Be(1);
        h.Broker.StopPlaceCount.Should().Be(1);
        h.Row.State.Should().Be(ProtectiveStopState.Active);
        h.Row.StopOrderId.Should().Be("stop-re-1");
    }

    // ---- 🔴 T-10-1128: #853 の経路（送信結果待ち・予約も記録も無い）・エントリー未約定・建玉 0 → 閉じない。約定後に張る ----

    [Fact]
    public async Task 予約の無い送信結果待ちでもエントリーが未約定なら記録を閉じず_約定後の巡回で逆指値を張る()
    {
        var entry = Guid.NewGuid();
        var h = NewHarness(row: PendingRowWithoutReservation(entry));
        h.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.Accepted, filled: 0);
        h.Broker.Positions = [];

        var first = await h.PatrolAsync();

        h.Row.State.Should().Be(ProtectiveStopState.Active, "#853 の「ガードが張り直す」はエントリーの約定後に成り立つ");
        first.Completed.Should().Be(0);
        h.Broker.StopPlaceCount.Should().Be(0);

        h.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.Filled, filled: 10);
        h.Broker.Positions = [Long(10)];

        var second = await h.PatrolAsync();

        second.Replaced.Should().Be(1);
        h.Broker.StopPlaceCount.Should().Be(1, "約定して建玉が現れた巡回で逆指値を張る");
        h.Row.StopOrderId.Should().NotBeEmpty();
    }

    // ---- T-10-1129: 発注記録が終端ならブローカーへ照会しない ----

    [Theory]
    [InlineData(OrderStatus.Filled, 10, true)]
    [InlineData(OrderStatus.Cancelled, 0, false)]
    public async Task 発注記録が終端ならエントリー注文をブローカーへ照会しない(
        OrderStatus recorded, int filled, bool expectRequery)
    {
        var h = NewHarness(entryRecordStatus: recorded, entryRecordFilled: filled);
        h.Broker.Orders[StopOrderId] = StopOrder(OrderStatus.Accepted);
        h.Broker.Positions = [];

        var result = await h.PatrolAsync();

        h.Broker.OrderQueries.Should().NotContain(EntryOrderId, "約定追跡が反映済みの終端を照会し直さない");
        h.Broker.Cancelled.Should().Equal([StopOrderId]);
        result.Completed.Should().Be(1);
        h.Broker.PositionCalls.Should().Be(expectRequery ? 2 : 1);
    }

    // ---- T-10-1130: 常駐の補償（EntryStateUnknown の発行に失敗したら記憶を捨て、次の巡回で出し直す） ----

    [Fact]
    public async Task 不明の通知を発行できなければ通知済みとして覚えず_次の巡回で出し直す()
    {
        var h = NewHarness(entryRecordStatus: null);
        h.Broker.Orders[StopOrderId] = StopOrder(OrderStatus.Accepted);
        h.Broker.Positions = [];

        var first = await h.PatrolAsync();
        UnknownNotices(first).Should().ContainSingle();
        h.Held.IsDue(h.EntryDecisionId, Now).Should().BeFalse("発行の前に通知済みと覚える");

        var publish = () => ProtectiveStopGuardService.PublishAllAsync(
            first.Events, _ => throw new InvalidOperationException("発行に失敗（テスト）"), h.Held);
        await publish.Should().ThrowAsync<InvalidOperationException>();

        h.Held.IsDue(h.EntryDecisionId, Now).Should().BeTrue("発行できなかった通知で 1 時間黙らない");
        h.Clock.UtcNow = Now.AddSeconds(30);
        var second = await h.PatrolAsync();
        UnknownNotices(second).Should().ContainSingle("次の巡回で出し直す");
    }
}
