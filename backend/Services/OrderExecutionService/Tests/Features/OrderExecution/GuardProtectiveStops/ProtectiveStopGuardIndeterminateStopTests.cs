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
using Xunit;
using Behavior = OrderExecutionService.Tests.StopLegScriptedBroker.StopBehavior;

namespace OrderExecutionService.Tests;

// 🔴 FR-10, FR-05, UC-02, #853, IADR-0210（2026-09-25 追記）, IADR-0428 決定1・決定2:
// **保護逆指値ガードの再発注が「届いたか不明」で終わったとき、成行へ倒さず、巡回を重ねても同じ逆指値を送り直さない。**
//
// #853 の追記（2026-09-19・#851 の 4 巡目監査）の実測: 逆指値の再発注＝届いたか不明、続く成行手仕舞い＝確実に未発注で 3 巡回まわすと、
//   marketUnavailable=True  patrol=1 stopSends=1 / patrol=2 stopSends=2 / patrol=3 stopSends=3（distinctStopIds=1）
// ——同じ StopDecisionId の逆指値が巡回ごとに 1 本ずつ増え、1 本目が届いていれば同じ建玉に逆指値が並ぶ（発火で反対建玉）。
// 本クラスは**巡回を重ねても逆指値の送信が 1 回のまま・成行は 0 回**であることを固定する（1→2→3 を 1→1→1 へ）。
public class ProtectiveStopGuardIndeterminateStopTests
{
    private static readonly DateTimeOffset Now = StopLegScriptedBroker.Now;

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed record Harness(
        ProtectiveStopGuard Guard,
        StopLegScriptedBroker Broker,
        InMemoryProtectiveStopOrderStore Stops,
        InMemoryExecutedOrderStore Store,
        InMemoryOrderReservationStore Reservations,
        MutableClock Clock,
        Guid EntryDecisionId);

    // 元の逆指値（stop-1・試行 1）は失効している（Cancelled）＝巡回は ReplaceOrClose へ入る。
    private static Harness NewHarness(Behavior stop, bool marketUnavailable = false, ProtectiveStopOrder? seed = null)
    {
        var broker = new StopLegScriptedBroker { Stop = stop, MarketUnavailable = marketUnavailable };
        var stops = new InMemoryProtectiveStopOrderStore();
        var row = seed ?? LapsedRow(Guid.NewGuid());
        stops.Save(row);
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var clock = new MutableClock(Now);
        var guard = new ProtectiveStopGuard(
            broker, broker, stops, store, reservations, clock,
            heldCloseNotifications: new HeldCloseNotificationTracker(),
            closeRejections: new CloseRejectionTracker());
        return new Harness(guard, broker, stops, store, reservations, clock, row.EntryDecisionId);
    }

    private static ProtectiveStopOrder LapsedRow(Guid entry) =>
        new(entry, ProtectiveStopIds.StopDecisionId(entry, 1), StopLegScriptedBroker.LapsedStopOrderId,
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
            10, 950m, 1m, Attempt: 1, ProtectiveStopState.Active, Now.AddMinutes(-5), Now.AddMinutes(-5));

    // 送信結果待ちの行（試行 2 の逆指値を送ったが届いたか不明）。
    private static ProtectiveStopOrder PendingRow(Guid entry, int attempt = 2) =>
        new(entry, ProtectiveStopIds.StopDecisionId(entry, attempt), StopOrderId: string.Empty,
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
            10, 950m, 1m, attempt, ProtectiveStopState.Active, Now.AddMinutes(-5), Now.AddMinutes(-5),
            RemainingProtected: 10);

    // ---- 🔴 T-10-1063: #853 追記の再現（1→2→3 を 1→1→1 へ） ----

    [Theory]
    [InlineData(Behavior.Indeterminate, true)]
    [InlineData(Behavior.Indeterminate, false)]
    [InlineData(Behavior.Unclassified, true)]
    [InlineData(Behavior.Unclassified, false)]
    public async Task 届いたか不明な逆指値を巡回を重ねても送り直さず成行も送らない_否定形(Behavior stop, bool marketUnavailable)
    {
        var h = NewHarness(stop, marketUnavailable);
        var sends = new List<int>();

        for (var patrol = 1; patrol <= 3; patrol++)
        {
            await h.Guard.RunOnceAsync(batchSize: 10);
            sends.Add(h.Broker.StopPlaceCount);
        }

        sends.Should().Equal([1, 1, 1], "届いたか不明の逆指値は同じ StopDecisionId で送り直さない（#853 追記の 1→2→3）");
        h.Broker.StopDecisionIds.Distinct().Should().ContainSingle();
        h.Broker.MarketCloseCount.Should().Be(0, "逆指値が生きていれば、成行で建玉を落とすと逆指値が孤立して反対建玉を生む");
        h.Broker.CancelCount.Should().Be(0);

        var pendingLeg = ProtectiveStopIds.StopDecisionId(h.EntryDecisionId, attempt: 2);
        h.Reservations.Find(pendingLeg)!.State.Should().Be(OrderDispatchState.Reserved, "予約は解放も確定もしない");
        var row = h.Stops.Find(h.EntryDecisionId)!;
        row.State.Should().Be(ProtectiveStopState.Active, "記録は閉じない（巡回に残る）");
        row.IsStopDispatchPending.Should().BeTrue();
        row.StopDecisionId.Should().Be(pendingLeg);
        row.Attempt.Should().Be(2);
        h.Store.FindByDecisionId(pendingLeg).Should().BeNull("実在しない注文 ID の記録を作らない");
    }

    [Fact]
    public async Task 届いたか不明な逆指値の据え置きは最初の巡回で通知し_1時間は重ねず_その後は出し直す()
    {
        // T-10-1063（続き）: 無音にしない（Critical）が、30 秒の巡回ごとには重ねない（改定 9 と同じ作法）。
        var h = NewHarness(Behavior.Indeterminate, marketUnavailable: true);
        var pendingLeg = ProtectiveStopIds.StopDecisionId(h.EntryDecisionId, attempt: 2);

        var first = await h.Guard.RunOnceAsync(batchSize: 10);
        var second = await h.Guard.RunOnceAsync(batchSize: 10);
        h.Clock.UtcNow = Now + HeldCloseNotificationTracker.RenotifyInterval;
        var afterHour = await h.Guard.RunOnceAsync(batchSize: 10);

        var held = first.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle().Subject;
        held.Remediation.Should().Be(ProtectiveStopRemediation.StopDispatchIndeterminate);
        held.Cause.Should().Be(ProtectiveStopLossCause.LapsedInFlight);
        held.CloseDecisionId.Should().Be(pendingLeg, "運ぶのは逆指値レグの DecisionId（台帳が逆指値の承認行として押さえる）");
        held.CloseIntent!.Side.Should().Be(TradeSide.Sell);
        held.CloseIntent.Quantity.Should().Be(10);
        first.Unknown.Should().Be(1, "据え置き＝不明（手仕舞いにも完了にも数えない）");
        first.ClosedOut.Should().Be(0);

        second.Events.Should().BeEmpty("1 時間は重ねない");
        afterHour.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle()
            .Which.CloseDecisionId.Should().Be(pendingLeg, "同じ 1 本の逆指値について出し直す（新しい発注ではない）");
        h.Broker.StopPlaceCount.Should().Be(1);
    }

    // ---- T-10-1064: 確実に未発注は従来どおり（予約は解放） ----

    [Theory]
    [InlineData(Behavior.Unavailable)]
    [InlineData(Behavior.Reject)]
    public async Task 確実に未発注の逆指値は従来どおり成行へ進み予約を解放する(Behavior stop)
    {
        var h = NewHarness(stop);

        var result = await h.Guard.RunOnceAsync(batchSize: 10);

        h.Broker.MarketCloseCount.Should().Be(1, "確実に未発注（接続確立の失敗・確認できた拒否）は従来どおり成行で手仕舞う");
        result.ClosedOut.Should().Be(1);
        h.Stops.Find(h.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        h.Reservations.Find(ProtectiveStopIds.StopDecisionId(h.EntryDecisionId, attempt: 2))
            .Should().BeNull("確実に未発注の逆指値の予約は解放する（据え置く理由が無い）");
    }

    [Fact]
    public async Task 確実に未発注が続くなら従来どおり次の巡回で逆指値を送り直せる()
    {
        // T-10-1064（対）: 変えてはいけない側。接続確立の失敗と成行の失敗が続くときは、従来どおり巡回ごとに再試行する
        // （送っていないものは送り直してよい）。予約を据え置く是正がこちらへ波及して保護の再試行を止めないこと。
        var h = NewHarness(Behavior.Unavailable, marketUnavailable: true);

        await h.Guard.RunOnceAsync(batchSize: 10);
        await h.Guard.RunOnceAsync(batchSize: 10);

        h.Broker.StopPlaceCount.Should().Be(2);
        h.Broker.MarketCloseCount.Should().Be(2);
        h.Stops.Find(h.EntryDecisionId)!.StopOrderId.Should().Be(StopLegScriptedBroker.LapsedStopOrderId);
    }

    [Fact]
    public async Task 受理された再発注は予約を注文IDつきで確定する()
    {
        // T-10-1064（受理側）: 3 相の相 4。結果を保存してから予約を確定する。
        var h = NewHarness(Behavior.Accept);

        var result = await h.Guard.RunOnceAsync(batchSize: 10);

        result.Replaced.Should().Be(1);
        var leg = ProtectiveStopIds.StopDecisionId(h.EntryDecisionId, attempt: 2);
        var reservation = h.Reservations.Find(leg)!;
        reservation.State.Should().Be(OrderDispatchState.Completed);
        reservation.BrokerOrderId.Should().Be(h.Stops.Find(h.EntryDecisionId)!.StopOrderId);
    }

    // ---- T-10-1065: 突合が記録を作ったら注文 ID を採用する ----

    [Fact]
    public async Task 送信結果待ちに生きた記録が現れたら注文IDを採用し送り直さない()
    {
        var entry = Guid.NewGuid();
        var h = NewHarness(Behavior.Accept, seed: PendingRow(entry));
        var leg = ProtectiveStopIds.StopDecisionId(entry, attempt: 2);
        h.Reservations.TryReserve(leg, Now.AddHours(-3));
        // 突合（client order id）が発注済みと確定し、記録を保存して予約を確定した（OrderReservationReconciler の Placed）。
        h.Store.Save(new ExecutionRecord(
            leg, "stop-found", "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, PositionEffect.Open,
            10, 950m, 0, 0m, OrderStatus.Accepted, 0m, Now.AddHours(-3)));
        h.Reservations.MarkCompleted(leg, "stop-found", Now.AddMinutes(-1));
        h.Broker.Orders["stop-found"] = new BrokerOrder(
            "stop-found", StopLegScriptedBroker.CloseIntent(10), OrderStatus.Accepted, 0, 0m, Now, null);

        var adopted = await h.Guard.RunOnceAsync(batchSize: 10);
        var next = await h.Guard.RunOnceAsync(batchSize: 10);

        adopted.Replaced.Should().Be(1);
        var placed = adopted.Events.OfType<ProtectiveStopPlaced>().Should().ContainSingle().Subject;
        placed.StopDecisionId.Should().Be(leg);
        placed.StopOrderId.Should().Be("stop-found");
        h.Stops.Find(entry)!.StopOrderId.Should().Be("stop-found");
        h.Stops.Find(entry)!.IsStopDispatchPending.Should().BeFalse();
        next.StillActive.Should().Be(1, "採用した逆指値は通常どおり照会され、建玉があれば維持される");
        h.Broker.StopPlaceCount.Should().Be(0, "逆指値は既にある。送り直さない");
        h.Broker.MarketCloseCount.Should().Be(0);
    }

    [Fact]
    public async Task 送信結果待ちに生きていない記録が現れたら採用して次の試行で張り直す()
    {
        // T-10-1065（続き）: 突合が見つけた注文が拒否されていた（届いたが受理されなかった）。採用した行で通常の失効として扱う。
        var entry = Guid.NewGuid();
        var h = NewHarness(Behavior.Accept, seed: PendingRow(entry));
        var leg = ProtectiveStopIds.StopDecisionId(entry, attempt: 2);
        h.Reservations.TryReserve(leg, Now.AddHours(-3));
        h.Store.Save(new ExecutionRecord(
            leg, "stop-rejected", "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, PositionEffect.Open,
            10, 950m, 0, 0m, OrderStatus.Rejected, 0m, Now.AddHours(-3)));
        h.Reservations.MarkCompleted(leg, "stop-rejected", Now.AddMinutes(-1));
        h.Broker.Orders["stop-rejected"] = new BrokerOrder(
            "stop-rejected", StopLegScriptedBroker.CloseIntent(10), OrderStatus.Rejected, 0, 0m, Now, Now);

        var result = await h.Guard.RunOnceAsync(batchSize: 10);

        result.Replaced.Should().Be(1);
        h.Broker.StopDecisionIds.Should().Equal([ProtectiveStopIds.StopDecisionId(entry, attempt: 3)], "次の試行の新しいレグで張る");
        h.Stops.Find(entry)!.Attempt.Should().Be(3);
        result.Events.OfType<ProtectiveStopPlaced>().Should().ContainSingle()
            .Which.StopDecisionId.Should().NotBe(leg, "拒否された逆指値を「張った」とは言わない");
    }

    // ---- T-10-1066: 予約が解放された送信結果待ちは未発注として扱う ----

    [Fact]
    public async Task 予約が解放された送信結果待ちは未発注として次の試行で張り直す()
    {
        var entry = Guid.NewGuid();
        var h = NewHarness(Behavior.Accept, seed: PendingRow(entry));

        var result = await h.Guard.RunOnceAsync(batchSize: 10);

        result.Replaced.Should().Be(1);
        h.Broker.StopDecisionIds.Should().Equal([ProtectiveStopIds.StopDecisionId(entry, attempt: 3)]);
        h.Stops.Find(entry)!.StopOrderId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task 予約が解放された送信結果待ちで建玉が無ければ完了する()
    {
        var entry = Guid.NewGuid();
        var h = NewHarness(Behavior.Accept, seed: PendingRow(entry));
        h.Broker.Positions = [];

        var result = await h.Guard.RunOnceAsync(batchSize: 10);

        result.Completed.Should().Be(1);
        h.Broker.StopPlaceCount.Should().Be(0);
        h.Stops.Find(entry)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // ---- 🔴 T-10-1067: 据え置き中に建玉が消えても記録を閉じない ----

    [Fact]
    public async Task 据え置き中に建玉が消えても記録を完了させず取消も送らない_否定形()
    {
        // 逆指値が生きているかもしれないのに記録を閉じると、誰も取り消さない孤立注文になり、発火で反対建玉を生む。
        var entry = Guid.NewGuid();
        var h = NewHarness(Behavior.Accept, seed: PendingRow(entry));
        h.Reservations.TryReserve(ProtectiveStopIds.StopDecisionId(entry, attempt: 2), Now.AddMinutes(-5));
        h.Broker.Positions = [];

        var result = await h.Guard.RunOnceAsync(batchSize: 10);

        result.Unknown.Should().Be(1);
        result.Completed.Should().Be(0);
        h.Stops.Find(entry)!.State.Should().Be(ProtectiveStopState.Active);
        h.Broker.CancelCount.Should().Be(0);
        h.Broker.StopPlaceCount.Should().Be(0);
        h.Broker.MarketCloseCount.Should().Be(0);
    }

    [Fact]
    public async Task 再発注の予約そのものが落ちたら送らず成行も送らず人へ知らせ記録は巡回に残す_否定形()
    {
        // T-10-1068（続き・PR #1005 監査 3）: 予約表が落ちた（DB 障害）。逆指値も成行も送らず、Critical の保護喪失（None）を出し、
        // 行は変えない（失効した元の逆指値を指したまま＝次の巡回で改めて評価する）。
        var row = LapsedRow(Guid.NewGuid());
        var broker = new StopLegScriptedBroker { Stop = Behavior.Accept };
        var stops = new InMemoryProtectiveStopOrderStore();
        stops.Save(row);
        var reservations = new ThrowingReserveStore(
            new InMemoryOrderReservationStore(), ProtectiveStopIds.StopDecisionId(row.EntryDecisionId, attempt: 2));
        var clock = new MutableClock(Now);
        var guard = new ProtectiveStopGuard(
            broker, broker, stops, new InMemoryExecutedOrderStore(), reservations, clock,
            heldCloseNotifications: new HeldCloseNotificationTracker(), closeRejections: new CloseRejectionTracker());

        var result = await guard.RunOnceAsync(batchSize: 10);

        broker.StopPlaceCount.Should().Be(0);
        broker.MarketCloseCount.Should().Be(0);
        result.CloseFailed.Should().Be(1, "解消していない（手仕舞いにも完了にも数えない）");
        var lost = result.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle().Subject;
        lost.Remediation.Should().Be(ProtectiveStopRemediation.StopReservationFailed, "予約できず送っていない（「解消にも失敗」ではない）");
        lost.Cause.Should().Be(ProtectiveStopLossCause.LapsedInFlight);
        lost.CloseIntent.Should().BeNull();
        var after = stops.Find(row.EntryDecisionId)!;
        after.State.Should().Be(ProtectiveStopState.Active, "巡回に残る");
        after.StopOrderId.Should().Be(StopLegScriptedBroker.LapsedStopOrderId);

        // PR #1005 再監査: 読めるが書けない障害が続いても、通知は巡回（30 秒）ごとに重ねず、1 時間ごとに出し直す。
        clock.UtcNow = Now.AddSeconds(30);
        var again = await guard.RunOnceAsync(batchSize: 10);
        again.CloseFailed.Should().Be(1);
        again.Events.Should().BeEmpty("1 時間は同じ通知を重ねない");
        clock.UtcNow = Now + HeldCloseNotificationTracker.RenotifyInterval;
        (await guard.RunOnceAsync(batchSize: 10)).Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle()
            .Which.Remediation.Should().Be(ProtectiveStopRemediation.StopReservationFailed);
        broker.StopPlaceCount.Should().Be(0);
        broker.MarketCloseCount.Should().Be(0);
    }

    // ---- T-10-1077: 発行できなかった据え置きの通知を「通知済み」と覚えない ----

    [Fact]
    public async Task 据え置きの通知を発行できなければ次の巡回で出し直す()
    {
        // 常駐（ProtectiveStopGuardService.PublishAllAsync）は未発行分の記憶を消してから投げ直す（改定 9 と同じ補償）。
        // 消さないと、Critical も台帳の押さえ（逆指値レグの承認行）も出ないまま 1 時間黙る。
        var tracker = new HeldCloseNotificationTracker();
        var broker = new StopLegScriptedBroker { Stop = Behavior.Indeterminate };
        var stops = new InMemoryProtectiveStopOrderStore();
        var row = LapsedRow(Guid.NewGuid());
        stops.Save(row);
        var clock = new MutableClock(Now);
        var guard = new ProtectiveStopGuard(
            broker, broker, stops, new InMemoryExecutedOrderStore(), new InMemoryOrderReservationStore(), clock,
            heldCloseNotifications: tracker, closeRejections: new CloseRejectionTracker());

        var produced = await guard.RunOnceAsync(batchSize: 10);
        var publish = async () => await ProtectiveStopGuardService.PublishAllAsync(
            produced.Events, _ => throw new InvalidOperationException("メッセージ基盤へ発行できない（テスト）"), tracker);
        await publish.Should().ThrowAsync<InvalidOperationException>();

        clock.UtcNow = Now.AddSeconds(30);
        var retried = await guard.RunOnceAsync(batchSize: 10);

        retried.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle()
            .Which.Remediation.Should().Be(ProtectiveStopRemediation.StopDispatchIndeterminate);
        broker.StopPlaceCount.Should().Be(1, "出し直すのは通知だけ。逆指値は送り直さない");
    }

    // ---- T-10-1068: 予約が既にある（送信中に止まった）→ 送らずに送信結果待ちへ ----

    [Fact]
    public async Task 逆指値の予約が既にあれば送らずに送信結果待ちへ移り通知する()
    {
        var h = NewHarness(Behavior.Accept);
        var leg = ProtectiveStopIds.StopDecisionId(h.EntryDecisionId, attempt: 2);
        h.Reservations.TryReserve(leg, Now.AddMinutes(-2)); // 以前の巡回が送信に着手したまま止まった

        var result = await h.Guard.RunOnceAsync(batchSize: 10);

        h.Broker.StopPlaceCount.Should().Be(0, "送信中か成否不明。重ねて送らない");
        h.Broker.MarketCloseCount.Should().Be(0);
        h.Stops.Find(h.EntryDecisionId)!.IsStopDispatchPending.Should().BeTrue();
        result.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle()
            .Which.Remediation.Should().Be(ProtectiveStopRemediation.StopDispatchIndeterminate);
    }
}
