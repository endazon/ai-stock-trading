using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Hosted;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
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
// 通知の間隔を外す（T-10-1124）／終端の記録でもブローカーへ照会する（T-10-1129）／常駐の補償から新しい値を外す（T-10-1130）／
// 終端の「1 株でも約定」を外す（T-10-1136）／一部約定の Warning を Debug へ戻す（T-10-1137）／不明が解けても記憶を残す（T-10-1138）。
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
            // #1449, IADR-0576 決定2: 本物の証券会社と同じく、取消した注文は照会で終端（Cancelled）に見える（ガードは照会で確かめてから完了する）。
            if (Orders.TryGetValue(orderId, out var working) && working is { Status: OrderStatus.Accepted or OrderStatus.PartiallyFilled })
                Orders[orderId] = working with { Status = OrderStatus.Cancelled, CompletedAt = working.PlacedAt };
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
        OrderStatus? entryRecordStatus = OrderStatus.Accepted, int entryRecordFilled = 0, ProtectiveStopOrder? row = null,
        ILogger<ProtectiveStopGuard>? logger = null)
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
            broker, broker, stops, store, reservations, clock, AlwaysOpenSessionGate.Instance, logger,
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
    // ---- 🔴 T-10-1136（PR #1014 監査 N2）: 一部約定の後に残りが取り消されたエントリーは「建った」——建玉を照会し直す ----

    [Theory]
    [InlineData(true)]   // 約定追跡が反映済み（発注記録が終端）
    [InlineData(false)]  // 発注記録は非終端のまま（注文照会が終端を返す）
    public async Task 一部約定の後に残りが取り消されたエントリーは建玉を照会し直し_建玉があれば取り消さない_否定形(bool recordTerminal)
    {
        var h = recordTerminal
            ? NewHarness(entryRecordStatus: OrderStatus.Cancelled, entryRecordFilled: 5)
            : NewHarness();
        h.Broker.Orders[StopOrderId] = StopOrder(OrderStatus.Accepted);
        h.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.Cancelled, filled: 5);
        h.Broker.PositionSequence.Enqueue([]);         // 巡回の最初の照会（残りの取消・約定の反映より前）
        h.Broker.PositionSequence.Enqueue([Long(5)]);  // 照会し直すと約定分 5 株がある

        var result = await h.PatrolAsync();

        h.Broker.Cancelled.Should().BeEmpty("5 株の建玉が実在する——「建っていない」と読んで逆指値を取り消さない");
        h.Row.State.Should().Be(ProtectiveStopState.Active);
        result.StillActive.Should().Be(1);
        h.Broker.PositionCalls.Should().Be(2, "約定 1 株以上の終端は「建った」として建玉を照会し直す");
    }

    // ---- T-10-1137（PR #1014 監査 N3）: 一部約定のまま生きていて建玉 0 → 取り消さないが Warning で残す ----

    [Fact]
    public async Task 一部約定のまま生きているエントリーで建玉が0ならWarningを残す()
    {
        var logger = new SoftwareStopLivenessReporterTests.RecordingLogger<ProtectiveStopGuard>();
        var h = NewHarness(logger: logger);
        h.Broker.Orders[StopOrderId] = StopOrder(OrderStatus.Accepted);
        h.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.PartiallyFilled, filled: 4);
        h.Broker.Positions = [];

        await h.PatrolAsync();

        h.Broker.Cancelled.Should().BeEmpty();
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("一部約定のまま生きています"),
            "逆指値が承認数量のまま残る状態を Debug に埋もれさせない");

        // 約定 0 の未約定は従来どおり Debug（毎巡回の Warning にしない）。
        var quiet = new SoftwareStopLivenessReporterTests.RecordingLogger<ProtectiveStopGuard>();
        var h2 = NewHarness(logger: quiet);
        h2.Broker.Orders[StopOrderId] = StopOrder(OrderStatus.Accepted);
        h2.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.Accepted, filled: 0);
        h2.Broker.Positions = [];

        await h2.PatrolAsync();

        quiet.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    // ---- T-10-1138（PR #1014 監査 N4）: 不明 → 未約定と分かったら通知の記憶を捨て、再び不明になれば間隔を待たずに知らせる ----

    [Fact]
    public async Task 不明が解けたら通知の記憶を捨て_再び不明になれば1時間を待たずに知らせる()
    {
        var h = NewHarness();
        h.Broker.Orders[StopOrderId] = StopOrder(OrderStatus.Accepted);
        h.Broker.Positions = [];
        // エントリー注文は未登録＝照会が答えない（不明）。

        var first = await h.PatrolAsync();
        UnknownNotices(first).Should().ContainSingle();

        h.Clock.UtcNow = Now.AddMinutes(5);
        h.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.Accepted, filled: 0); // 照会が戻った（未約定）
        var second = await h.PatrolAsync();
        UnknownNotices(second).Should().BeEmpty();
        second.StillActive.Should().Be(1);

        h.Clock.UtcNow = Now.AddMinutes(10);
        h.Broker.Orders.Remove(EntryOrderId); // 再び答えない
        var third = await h.PatrolAsync();
        UnknownNotices(third).Should().ContainSingle("別の不明の始まりであり、前回の通知から 1 時間を待たせない");
    }

    // ---- 🔴 FR-10, ADR-0040, #1454, IADR-0580: 窓開けで逆指値がエントリーより先に約定する（T-10-3025〜T-10-3032） ----
    //
    // S0/S3 は承認数量の逆指値を、未約定のエントリー（指値）と同時に板に置く。窓開け（寄り付きが損切りラインの下）では、
    // 買いの指値と売りの逆指値がどちらも発火し得る。逆指値が先に約定すると、建玉 0 からの売り＝反対方向の建玉（売り建玉）が生じる。
    // #1423（試験の穴 3）で現行の挙動（エントリーを見ずに記録を完了し、売り建玉を誰も知らせない）を T-10-3025・T-10-3026 に固定し、
    // #1454 で是正した。期待は是正後の挙動へ書き換えた（決定 1: エントリーが生きていれば据え置いて知らせる／決定 2: 逆方向の S0 の足跡がある
    // 群は帰属不明の検知の対象にする）。自動の買い戻し・エントリーの取消はしない（別途の判断）。
    //
    // 殺す変異（手で入れて赤を確かめた）: Filled の分岐のエントリー照会を外す（T-10-3025 / 3026 / 3027 / 3028）／通知の間隔を外す（T-10-3027）／
    // 検知の逆方向の足跡を外す（T-10-3026 / 3031）／印の読み戻しの方向（WatchedSide）を EntrySide に戻す（T-10-3026。同じ方向の群が
    // 印を毎回消して、売り建玉が残るあいだ巡回ごとに鳴り直す）。

    private static BrokerOrder FilledStop(int filled) =>
        StopOrder(OrderStatus.Filled) with { FilledQuantity = filled, AveragePrice = 940m };

    private static BrokerPositionSnapshot Short(int qty) => new("AAPL", Market.UnitedStates, -qty, 940m);

    private static IEnumerable<ProtectiveStopCoverageLost> FilledBeforeEntryNotices(ProtectiveStopGuardResult result) =>
        result.Events.OfType<ProtectiveStopCoverageLost>()
            .Where(e => e.Remediation == ProtectiveStopRemediation.StopFilledBeforeEntry);

    private static IEnumerable<SoftwareStopExecuted> UnattributedNotices(IEnumerable<object> events) =>
        events.OfType<SoftwareStopExecuted>().Where(e => e.Outcome == SoftwareStopOutcome.UnattributedPosition);

    // T-10-3025: 逆指値が先に約定（売り建玉 10）・エントリーは未約定のまま生きている → 記録を閉じずに据え置き、保護喪失の事実を出す。
    // エントリーが後で全量約定して純額 0 に戻ったら完了する。
    [Fact]
    public async Task T_10_3025_窓開けで逆指値が先に約定しエントリーが生きていれば_記録を閉じずに知らせ_全量約定で完了する()
    {
        var h = NewHarness();
        h.Broker.Orders[StopOrderId] = FilledStop(10);
        h.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.Accepted, filled: 0);
        h.Broker.Positions = [Short(10)]; // 建玉 0 からの売りが約定した（反対方向の建玉）

        var result = await h.PatrolAsync();

        result.Completed.Should().Be(0, "エントリーが生きているあいだは損切りの成立と読まない");
        h.Row.State.Should().Be(ProtectiveStopState.Active, "閉じると、エントリーが約定しないまま終わったときに売り建玉が巡回から外れる");
        h.Broker.OrderQueries.Should().Contain(EntryOrderId, "完了させる前にエントリーの状態を照会する");
        h.Broker.Cancelled.Should().BeEmpty("エントリーの取消は別途の判断（自動ではしない）");
        h.Broker.MarketCloseCount.Should().Be(0, "売り建玉の自動の買い戻しは別途の判断（自動ではしない）");
        h.Broker.StopPlaceCount.Should().Be(0, "逆指値を張り直さない");
        var notice = FilledBeforeEntryNotices(result).Should().ContainSingle("保護喪失の事実を出して人手対応を求める").Subject;
        notice.Quantity.Should().Be(10);
        notice.CloseDecisionId.Should().Be(h.Row.StopDecisionId, "相関は約定した逆指値レグ");
        notice.CloseIntent.Should().BeNull("何も送っていない（台帳に処理中の決済を押さえさせない）");
        notice.Cause.Should().Be(ProtectiveStopLossCause.LapsedInFlight);

        // エントリーが後で全量約定すると、建玉は純額 0 に戻る。終端を見た巡回で完了する。
        h.Clock.UtcNow = Now.AddMinutes(1);
        h.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.Filled, filled: 10);
        h.Broker.Positions = [];

        var after = await h.PatrolAsync();

        after.Completed.Should().Be(1);
        h.Row.State.Should().Be(ProtectiveStopState.Completed);
        after.Events.Should().BeEmpty("純額 0 に戻った。知らせることは無い");
        h.Broker.MarketCloseCount.Should().Be(0);
    }

    // T-10-3026: 同上で、エントリーが約定しないまま終わる（失効）→ 記録は完了する。売り建玉 10 は帰属不明として知らせ続ける
    // （ガードの最初の巡回で 1 回、記録の完了後は建玉観測の常駐の検知が 1 時間ごと。完了した S0 の記録が逆方向の足跡になる）。
    [Fact]
    public async Task T_10_3026_逆指値が先に約定しエントリーが約定せず失効したら_記録を完了し売り建玉を帰属不明として知らせ続ける()
    {
        var h = NewHarness();
        h.Broker.Orders[StopOrderId] = FilledStop(10);
        h.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.Accepted, filled: 0);
        h.Broker.Positions = [Short(10)];

        var first = await h.PatrolAsync();
        first.Completed.Should().Be(0);
        FilledBeforeEntryNotices(first).Should().ContainSingle();
        UnattributedNotices(first.Events).Should().ContainSingle(
            "S0 だけの銘柄でも、エントリーと逆方向の純額（売り建玉）は帰属不明として知らせる").Which.Quantity.Should().Be(10);

        h.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.Expired, filled: 0);
        h.Clock.UtcNow = Now.AddMinutes(10);

        var after = await h.PatrolAsync();

        after.Completed.Should().Be(1, "エントリーが終端した。従来どおり完了させる");
        h.Row.State.Should().Be(ProtectiveStopState.Completed);
        after.Events.Should().BeEmpty("同じ株数の帰属不明は間隔の内は重ねない・据え置きは解けた");
        h.Row.UnattributedNotifiedQuantity.Should().Be(10, "印が残る＝完了した後も検知が同じ群を訪れ続ける");
        h.Broker.Cancelled.Should().BeEmpty();
        h.Broker.MarketCloseCount.Should().Be(0, "反対方向の建玉を自動では買い戻さない（検知であって是正ではない）");

        // 完了した記録は巡回に載らない（ガードは建玉を照会しない）。建玉観測の常駐が同じ検知を行う。
        var resident = new List<object>();
        ProtectiveStopNetting.DetectUnattributedPositions(
            [Short(10)], h.Stops.FindActive(10), h.Stops, h.Store, Now.AddMinutes(70), resident);
        UnattributedNotices(resident).Should().ContainSingle("売り建玉が残るあいだ 1 時間ごとに知らせる（無通知で残らない）")
            .Which.EntryDecisionId.Should().Be(h.EntryDecisionId, "代表は逆方向の S0 の記録");
    }

    // T-10-3027: 据え置きの通知は巡回ごとに重ねない（このプロセスで 1 回・以後 1 時間ごと）。
    [Fact]
    public async Task T_10_3027_逆指値が先に約定した据え置きの通知は巡回ごとに重ねず_1時間ごとに出し直す()
    {
        var h = NewHarness();
        h.Broker.Orders[StopOrderId] = FilledStop(10);
        h.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.Accepted, filled: 0);
        h.Broker.Positions = [Short(10)];

        FilledBeforeEntryNotices(await h.PatrolAsync()).Should().ContainSingle();

        h.Clock.UtcNow = Now.AddMinutes(5);
        var second = await h.PatrolAsync();
        FilledBeforeEntryNotices(second).Should().BeEmpty("30 秒の巡回ごとに Critical を重ねない");
        second.StillActive.Should().Be(1);
        h.Row.State.Should().Be(ProtectiveStopState.Active);

        h.Clock.UtcNow = Now.AddHours(1);
        FilledBeforeEntryNotices(await h.PatrolAsync()).Should().ContainSingle("通知を 1 回見逃すと売り建玉が無期限に残る");
    }

    // T-10-3028: エントリーの状態が分からない（照会が答えない）・売り建玉が見えている → 据え置いて知らせる（無音にしない側）。
    [Fact]
    public async Task T_10_3028_逆指値が約定しエントリーの状態が不明で売り建玉が見えるなら_記録を閉じずに知らせる()
    {
        var h = NewHarness();
        h.Broker.Orders[StopOrderId] = FilledStop(10);
        // エントリー注文は未登録＝照会が答えない（不明）。
        h.Broker.Positions = [Short(10)];

        var result = await h.PatrolAsync();

        result.Completed.Should().Be(0);
        result.Unknown.Should().Be(1);
        h.Row.State.Should().Be(ProtectiveStopState.Active);
        FilledBeforeEntryNotices(result).Should().ContainSingle();
        h.Broker.Cancelled.Should().BeEmpty();
        h.Broker.MarketCloseCount.Should().Be(0);
    }

    // T-10-3029: エントリーの状態が不明でも、反対方向の建玉が見えなければ従来どおり完了する（通常の損切りの成立と区別が付かない）。
    [Fact]
    public async Task T_10_3029_逆指値が約定しエントリーの状態が不明でも反対建玉が無ければ従来どおり完了する()
    {
        var h = NewHarness();
        h.Broker.Orders[StopOrderId] = FilledStop(10);
        h.Broker.Positions = [];

        var result = await h.PatrolAsync();

        result.Completed.Should().Be(1);
        h.Row.State.Should().Be(ProtectiveStopState.Completed);
        result.Events.Should().BeEmpty();
    }

    // T-10-3030: 通常の損切り（エントリーの記録が終端の Filled・建玉 0）→ 従来どおり完了。エントリーをブローカーへ照会しない（記録で足りる）。
    [Fact]
    public async Task T_10_3030_通常の損切りの成立は従来どおり完了し_エントリーをブローカーへ照会しない()
    {
        var h = NewHarness(entryRecordStatus: OrderStatus.Filled, entryRecordFilled: 10);
        h.Broker.Orders[StopOrderId] = FilledStop(10);
        h.Broker.Positions = [];

        var result = await h.PatrolAsync();

        result.Completed.Should().Be(1);
        h.Row.State.Should().Be(ProtectiveStopState.Completed);
        h.Broker.OrderQueries.Should().NotContain(EntryOrderId, "終端の発注記録で足りる（平常の経路で照会を増やさない）");
        result.Events.Should().BeEmpty();
    }

    // ---- 帰属不明の検知（建玉観測の常駐と同じ関数を直接呼ぶ）----

    private static ProtectiveStopOrder CompletedS0(Guid entry, DateTimeOffset at) =>
        LiveRow(entry) with { State = ProtectiveStopState.Completed, UpdatedAt = at };

    // T-10-3031: 完了した S0 の買いの記録があり、売り建玉 10 が見える → 知らせる。印は S0 の行が持ち、1 時間は重ねない。
    // 売り建玉が消えたら（銘柄が照会から消えた巡回でも）印を消し、再発したら改めて知らせる。
    [Fact]
    public void T_10_3031_完了したS0の逆方向の売り建玉は検知が知らせ_印で重ねず_解消で印を消す()
    {
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var entry = Guid.NewGuid();
        stops.Save(CompletedS0(entry, Now.AddMinutes(-5)));

        var first = new List<object>();
        ProtectiveStopNetting.DetectUnattributedPositions([Short(10)], [], stops, store, Now, first);
        UnattributedNotices(first).Should().ContainSingle().Which.Quantity.Should().Be(10);
        stops.Find(entry)!.UnattributedNotifiedQuantity.Should().Be(10);
        stops.Find(entry)!.State.Should().Be(ProtectiveStopState.Completed, "状態は変えない（検知だけ）");

        var second = new List<object>();
        ProtectiveStopNetting.DetectUnattributedPositions([Short(10)], [], stops, store, Now.AddMinutes(10), second);
        UnattributedNotices(second).Should().BeEmpty("同じ株数・間隔の内は重ねない");

        var resolved = new List<object>();
        ProtectiveStopNetting.DetectUnattributedPositions([], [], stops, store, Now.AddMinutes(20), resolved);
        UnattributedNotices(resolved).Should().BeEmpty();
        stops.Find(entry)!.UnattributedNotifiedQuantity.Should().BeNull("印の群（逆方向）を訪れて消す");
        stops.Find(entry)!.UnattributedNotifiedAt.Should().BeNull();

        var again = new List<object>();
        ProtectiveStopNetting.DetectUnattributedPositions([Short(10)], [], stops, store, Now.AddMinutes(25), again);
        UnattributedNotices(again).Should().ContainSingle("解消のあと再発した同数は、間隔を待たずに知らせる");
    }

    // T-10-3032: 同じ方向の S0 だけの群（買いの記録と買い建玉の超過）は従来どおり触れない。逆方向の記録が無い売り建玉も触れない。
    [Fact]
    public void T_10_3032_同じ方向のS0だけの群と逆方向の記録が無い売り建玉は従来どおり知らせない()
    {
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var entry = Guid.NewGuid();
        stops.Save(LiveRow(entry));

        var sameSide = new List<object>();
        ProtectiveStopNetting.DetectUnattributedPositions([Long(25)], stops.FindActive(10), stops, store, Now, sameSide);
        UnattributedNotices(sameSide).Should().BeEmpty("S1 の足跡が無い同方向の群は S0 の近似の挙動を変えない（T-10-488）");

        var otherSymbol = new List<object>();
        ProtectiveStopNetting.DetectUnattributedPositions(
            [new BrokerPositionSnapshot("MSFT", Market.UnitedStates, -10, 400m)], stops.FindActive(10), stops, store, Now, otherSymbol);
        UnattributedNotices(otherSymbol).Should().BeEmpty("システムが建てていない銘柄の売り建玉（手動・他の実行機構）には触れない");
        stops.Find(entry)!.UnattributedNotifiedQuantity.Should().BeNull();
    }

    // T-10-3034: 常駐の補償（StopFilledBeforeEntry の発行に失敗したら記憶を捨て、次の巡回で出し直す。T-10-1130 と同じ形）。
    [Fact]
    public async Task T_10_3034_逆指値が先に約定した通知を発行できなければ通知済みとして覚えず_次の巡回で出し直す()
    {
        var h = NewHarness();
        h.Broker.Orders[StopOrderId] = FilledStop(10);
        h.Broker.Orders[EntryOrderId] = EntryOrder(OrderStatus.Accepted, filled: 0);
        h.Broker.Positions = [Short(10)];
        var stopDecisionId = h.Row.StopDecisionId;

        var first = await h.PatrolAsync();
        FilledBeforeEntryNotices(first).Should().ContainSingle();
        h.Held.IsDue(stopDecisionId, Now).Should().BeFalse("発行の前に通知済みと覚える");

        var publish = () => ProtectiveStopGuardService.PublishAllAsync(
            first.Events, _ => throw new InvalidOperationException("発行に失敗（テスト）"), h.Held);
        await publish.Should().ThrowAsync<InvalidOperationException>();

        h.Held.IsDue(stopDecisionId, Now).Should().BeTrue("発行できなかった通知で 1 時間黙らない");
        h.Clock.UtcNow = Now.AddSeconds(30);
        FilledBeforeEntryNotices(await h.PatrolAsync()).Should().ContainSingle("次の巡回で出し直す");
    }
}
