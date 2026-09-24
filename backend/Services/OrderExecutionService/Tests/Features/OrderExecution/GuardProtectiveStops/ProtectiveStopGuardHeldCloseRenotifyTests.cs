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

namespace OrderExecutionService.Tests;

// 🔴 T-10-451, FR-10, FR-11, UC-06, #848, IADR-0117（2026-09-19 追記・改定 9）, IADR-0210:
// **成行手仕舞いの据え置き（予約あり・記録なし＝届いたか不明）を無音にしない。**
//
// PR #851 の 4 巡目監査（非ブロッキング 1〜3）の実測:
//   1. Critical は不明になった巡回の 1 回きりで、以後は Warning ログだけ。見逃すと逆指値なしの建玉が無期限に残る
//     （Remediation=None は巡回ごとに Critical を出すので扱いが非対称だった）。
//   2. 送信中にプロセスが止まる（OperationCanceledException）と、再起動後のイベントは 0 件——Critical だけでなく
//      **CloseIntent も一度も発行されず、取引台帳が一切押さえない**（利用者の手仕舞いが 30 分待たずに通る）。
//   3. 巡回の結果を受け取ってから発行するまでのあいだに落ちても同じ。
//
// 是正: 「予約だけがある」分岐でも、①このプロセスが未通知、②前回の通知から 1 時間、のどちらかなら
// CloseDispatchIndeterminate（CloseIntent つき）を発行する。**成行も逆指値も送らない**ことを併せて固定する。
public class ProtectiveStopGuardHeldCloseRenotifyTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Start;
    }

    public enum CloseBehavior
    {
        Indeterminate,

        /// <summary>送信中に停止要求が来た（プロセスの停止）。送信は始まっている。</summary>
        CancelledMidSend,
    }

    // 逆指値の再発注は確認できた拒否（→ 成行手仕舞いへ落ちる）。元の逆指値は失効している。
    private sealed class GuardBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public CloseBehavior Close { get; set; } = CloseBehavior.Indeterminate;
        public int StopPlaceCount { get; private set; }
        public int MarketCloseCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("ガードは通常発注を行わない");

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default)
        {
            StopPlaceCount++;
            return Task.FromResult(new BrokerOrder(
                $"stop-re-{StopPlaceCount}", closeIntent, OrderStatus.Rejected, 0, 0m, Start, Start));
        }

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCloseCount++;
            throw Close == CloseBehavior.CancelledMidSend
                ? new OperationCanceledException("送信中に停止要求（テスト）")
                : new BrokerDispatchIndeterminateException(
                    "moomoo へ発注を送信しましたが結果を確認できませんでした（テスト）",
                    new TimeoutException("返信待ちタイムアウト（テスト）"));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(new BrokerOrder(
                orderId, new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                    BrokerProvider.MoomooSimulate, 10, 950m, PositionEffect.Close),
                OrderStatus.Cancelled, 0, 0m, Start, Start));

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BrokerPositionSnapshot>?>(
                [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 10, 1_000m)]);
    }

    // DB に残るもの（逆指値の記録・発注結果・予約）と、プロセス内にしか無いもの（通知の記憶）を分けて持つ。
    // 「再起動」＝ DB はそのまま、ガードと通知の記憶だけを作り直す。
    private sealed class World
    {
        public GuardBroker Broker { get; } = new();
        public InMemoryProtectiveStopOrderStore Stops { get; } = new();
        public InMemoryExecutedOrderStore Store { get; } = new();
        public InMemoryOrderReservationStore Reservations { get; } = new();
        public MutableClock Clock { get; } = new();
        public ProtectiveStopOrder Stop { get; }
        public HeldCloseNotificationTracker Tracker { get; private set; } = new();

        public World()
        {
            Stop = new ProtectiveStopOrder(
                Guid.NewGuid(), Guid.NewGuid(), "stop-1", "AAPL", Market.UnitedStates, TradeSide.Buy,
                ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, Attempt: 1,
                ProtectiveStopState.Active, Start.AddMinutes(-5), Start.AddMinutes(-5));
            Stops.Save(Stop);
        }

        public Guid CloseDecisionId => ProtectiveStopIds.CloseDecisionId(Stop.EntryDecisionId, attempt: 2);

        // 本番と同じ形: ガードは巡回ごとに作り直される（scoped）。通知の記憶だけがプロセスの寿命で生きる（singleton）。
        public Task<ProtectiveStopGuardResult> PatrolAsync(CancellationToken ct = default) =>
            new ProtectiveStopGuard(Broker, Broker, Stops, Store, Reservations, Clock, logger: null, Tracker)
                .RunOnceAsync(10, ct);

        public void RestartProcess() => Tracker = new HeldCloseNotificationTracker();
    }

    private static ProtectiveStopCoverageLost SingleHeldNotification(ProtectiveStopGuardResult result, World w)
    {
        var lost = result.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle().Which;
        lost.Remediation.Should().Be(ProtectiveStopRemediation.CloseDispatchIndeterminate,
            "None（解消に失敗）は CloseIntent を運ばず、読んだ人に手で成行を重ねさせる");
        lost.CloseDecisionId.Should().Be(w.CloseDecisionId, "同じ 1 本の成行の通知である（新しい発注ではない）");
        lost.CloseIntent.Should().NotBeNull("CloseIntent を運ぶ＝取引台帳が処理中の決済として押さえる");
        lost.CloseIntent!.Quantity.Should().Be(10);
        lost.CloseIntent.PositionEffect.Should().Be(PositionEffect.Close);
        return lost;
    }

    // ---- 1. 据え置きが続くあいだ、1 時間ごとに再発行する ----

    [Fact]
    public async Task 据え置きが1時間続いたら_Criticalを再発行する_それ未満では重ねない()
    {
        var w = new World();

        SingleHeldNotification(await w.PatrolAsync(), w); // 不明になった巡回

        // 1 時間未満: 30 秒の巡回ごとに Critical を重ねない。
        w.Clock.UtcNow = Start.AddSeconds(30);
        (await w.PatrolAsync()).Events.Should().BeEmpty();
        w.Clock.UtcNow = Start.AddMinutes(59).AddSeconds(59);
        (await w.PatrolAsync()).Events.Should().BeEmpty();

        // 1 時間: 再発行する（通知を 1 回見逃しても、逆指値なしの建玉が無期限に残らない）。
        w.Clock.UtcNow = Start.Add(HeldCloseNotificationTracker.RenotifyInterval);
        var renotified = SingleHeldNotification(await w.PatrolAsync(), w);
        renotified.OccurredAt.Should().Be(w.Clock.UtcNow);

        // 再発行の直後はまた 1 時間重ねない。さらに 1 時間で 3 回目。
        w.Clock.UtcNow = Start.AddHours(1).AddMinutes(30);
        (await w.PatrolAsync()).Events.Should().BeEmpty();
        w.Clock.UtcNow = Start.AddHours(2);
        SingleHeldNotification(await w.PatrolAsync(), w);

        // 🔴 結果の固定: 再通知は**注文を 1 本も増やさない**。
        w.Broker.MarketCloseCount.Should().Be(1, "再通知は通知と台帳への結線だけであり、成行を重ねない");
        w.Broker.StopPlaceCount.Should().Be(1, "逆指値も重ねない");
        w.Reservations.Find(w.CloseDecisionId)!.State.Should().Be(OrderDispatchState.Reserved);
        w.Stops.Find(w.Stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    [Fact]
    public void 再通知の間隔は1時間()
    {
        HeldCloseNotificationTracker.RenotifyInterval.Should().Be(TimeSpan.FromHours(1));
    }

    // ---- 2. 送信中にプロセスが止まった場合 ----

    // 🔴 T-10-451（監査の実測そのもの）: 送信中の停止では予約だけが残り、イベントは 1 通も出ていない。
    // 是正前は再起動後も 0 件のままで、**取引台帳が一切押さえなかった**。
    [Fact]
    public async Task 送信中にプロセスが止まっても_再起動後の最初の巡回で_CloseIntentつきのCriticalを発行する()
    {
        var w = new World();
        w.Broker.Close = CloseBehavior.CancelledMidSend;

        var stopped = async () => await w.PatrolAsync();
        await stopped.Should().ThrowAsync<OperationCanceledException>("停止要求は握りつぶさない");

        w.Broker.MarketCloseCount.Should().Be(1, "送信は始まっていた");
        w.Reservations.Find(w.CloseDecisionId)!.State.Should().Be(OrderDispatchState.Reserved,
            "予約は残る＝再起動後に撃ち直さない（ここまでは是正前も同じ）");

        // 再起動: DB（予約・逆指値の記録）はそのまま、プロセス内の記憶は消える。
        w.RestartProcess();
        w.Clock.UtcNow = Start.AddMinutes(2);
        var first = await w.PatrolAsync();

        SingleHeldNotification(first, w);
        first.Unknown.Should().Be(1, "据え置きのまま（完了を主張しない）");
        w.Broker.MarketCloseCount.Should().Be(1, "🔴 発行するのは通知と台帳への結線だけ。成行を撃ち直さない");
        w.Broker.StopPlaceCount.Should().Be(1, "成行が生きているかもしれない建玉へ逆指値を張らない");

        // 次の巡回では重ねない（このプロセスは通知済み）。
        w.Clock.UtcNow = Start.AddMinutes(2).AddSeconds(30);
        (await w.PatrolAsync()).Events.Should().BeEmpty();
    }

    // ---- 3. 発行の欠落 ----

    // 🔴 T-10-451: 巡回の結果を受け取ってから発行するまでのあいだにプロセスが落ちた（イベントは作られたが届いていない）。
    // 2 と同じ仕組みで塞がる——再起動で記憶が消えるので、最初の巡回が入口で発行し直す。
    [Fact]
    public async Task 発行の前にプロセスが落ちても_再起動後の最初の巡回で発行し直す()
    {
        var w = new World();

        var lostInFlight = await w.PatrolAsync(); // イベントは作られたが、発行の前にプロセスが落ちた。
        lostInFlight.Events.Should().ContainSingle();

        w.RestartProcess();
        w.Clock.UtcNow = Start.AddMinutes(1);

        SingleHeldNotification(await w.PatrolAsync(), w);
        w.Broker.MarketCloseCount.Should().Be(1);
    }

    // 🔴 T-10-451: プロセスは生きているが発行（PublishAsync）だけが失敗した。未発行分を「通知済み」と覚えたままだと、
    // Critical も台帳の押さえも出ないまま 1 時間黙る。常駐は未発行分の記憶を消してから投げ直す。
    [Fact]
    public async Task 発行に失敗したら通知済みと覚えず_次の巡回で発行し直す()
    {
        var w = new World();
        var produced = await w.PatrolAsync();

        var publish = async () => await ProtectiveStopGuardService.PublishAllAsync(
            produced.Events, _ => throw new InvalidOperationException("メッセージ基盤へ発行できない（テスト）"), w.Tracker);
        await publish.Should().ThrowAsync<InvalidOperationException>("発行の失敗は握りつぶさない（常駐が Error で記録する）");

        w.Clock.UtcNow = Start.AddSeconds(30);
        var retried = await w.PatrolAsync();

        SingleHeldNotification(retried, w);

        // 今度は発行できた → 以後 1 時間は重ねない。
        var published = new List<object>();
        await ProtectiveStopGuardService.PublishAllAsync(
            retried.Events, evt => { published.Add(evt); return ValueTask.CompletedTask; }, w.Tracker);
        published.Should().ContainSingle();
        w.Clock.UtcNow = Start.AddSeconds(60);
        (await w.PatrolAsync()).Events.Should().BeEmpty();
        w.Broker.MarketCloseCount.Should().Be(1);
    }

    // 途中で失敗したとき、**発行できた分**の記憶は消さない（消すと発行済みの Critical を次の巡回で重ねる）。
    [Fact]
    public async Task 発行の失敗で忘れるのは_未発行の据え置き通知だけ()
    {
        var tracker = new HeldCloseNotificationTracker();
        var intent = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 10, 950m, PositionEffect.Close);
        var (publishedId, failedId) = (Guid.NewGuid(), Guid.NewGuid());
        ProtectiveStopCoverageLost Held(Guid closeDecisionId) => new(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, ProtectiveStopLossCause.LapsedInFlight,
            ProtectiveStopRemediation.CloseDispatchIndeterminate, 10, closeDecisionId, intent, Start);
        tracker.MarkNotified(publishedId, Guid.NewGuid(), Start);
        tracker.MarkNotified(failedId, Guid.NewGuid(), Start);
        var calls = 0;

        var publish = async () => await ProtectiveStopGuardService.PublishAllAsync(
            [Held(publishedId), Held(failedId)],
            _ => ++calls == 1 ? ValueTask.CompletedTask : throw new InvalidOperationException("2 通目で失敗（テスト）"),
            tracker);
        await publish.Should().ThrowAsync<InvalidOperationException>();

        tracker.IsDue(publishedId, Start.AddSeconds(30)).Should().BeFalse("発行できた通知は重ねない");
        tracker.IsDue(failedId, Start.AddSeconds(30)).Should().BeTrue("発行できなかった通知は次の巡回で発行し直す");
    }

    // ---- 解決したら止まる ----

    [Fact]
    public async Task 手仕舞いが記録で確定したら_再通知を止める()
    {
        var w = new World();
        await w.PatrolAsync();

        // 突合（または人）が「発注済み」と解決し、発注結果の記録ができた。
        w.Store.Save(new ExecutionRecord(
            w.CloseDecisionId, "9049618348733212748", "AAPL", Market.UnitedStates, TradeSide.Sell,
            ProductType.Cash, PositionEffect.Close, 10, 950m, 10, 949m, OrderStatus.Filled, 0m, Start.AddMinutes(10)));

        w.Clock.UtcNow = Start.AddHours(3);
        var resolved = await w.PatrolAsync();

        resolved.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle()
            .Which.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed);
        w.Stops.Find(w.Stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);

        w.Clock.UtcNow = Start.AddHours(5);
        (await w.PatrolAsync()).Events.Should().BeEmpty("完了した記録は巡回対象から外れる");
        w.Tracker.IsDue(w.CloseDecisionId, w.Clock.UtcNow).Should().BeTrue("解決した手仕舞いの記憶は残さない");
    }
}
