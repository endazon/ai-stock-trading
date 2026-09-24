using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-752〜T-10-756, FR-10, FR-11, UC-02, UC-06, #938（PR #916 監査 F4・F5）, IADR-0369（2026-09-25 追記）:
// 保護逆指値ガードの**簿記と可観測性**。
//
// F4: プロセス内の記憶（CloseRejectionTracker の拒否の数えと通知時刻・HeldCloseNotificationTracker の据え置きの通知時刻）は、
//     記録を完了させる経路のうち Replaced と CompleteAsClosed でしか捨てられず、建玉消滅→取消・逆指値の Filled・
//     失効かつ建玉 0、そして**ガードの外**（乖離の取り込み）での完了では再起動まで残った。誤動作ではないが辞書が単調に増える。
// F5: 成行手仕舞いも確実に未発注で失敗した（Remediation=None）巡回を ClosedOut（＝「手仕舞い」）に数えていた。
//
// **時計は注入**であり実時間を待たない。
public class ProtectiveStopGuardCompletionBookkeepingTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 25, 14, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Start;
    }

    public enum CloseBehavior
    {
        /// <summary>確認できた拒否（終端が返る）。</summary>
        Rejected,

        /// <summary>送信済み・届いたか不明。</summary>
        Indeterminate,

        /// <summary>接続確立の失敗＝確実に未発注。</summary>
        Unavailable,

        /// <summary>受理されて全量約定。</summary>
        Filled,
    }

    // 逆指値の再発注は拒否（→ 成行手仕舞いへ落ちる）。元の逆指値の状態・建玉・成行の挙動を巡回ごとに差し替えられる。
    private sealed class GuardBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public CloseBehavior Close { get; set; } = CloseBehavior.Rejected;

        /// <summary>元の逆指値（stop-1）の照会結果の状態。既定は失効（Cancelled）＝毎巡回 ReplaceOrClose へ入る。</summary>
        public OrderStatus StopStatus { get; set; } = OrderStatus.Cancelled;

        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [new("AAPL", Market.UnitedStates, 10, 1_000m)];

        public int MarketCloseCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("ガードは通常発注を行わない");

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            Task.FromResult(new BrokerOrder(
                "stop-re", closeIntent, OrderStatus.Rejected, 0, 0m, DateTimeOffset.MinValue, DateTimeOffset.MinValue));

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCloseCount++;
            return Close switch
            {
                CloseBehavior.Rejected => Task.FromResult(new BrokerOrder(
                    $"close-{MarketCloseCount}", closeIntent, OrderStatus.Rejected, 0, 0m,
                    DateTimeOffset.MinValue, DateTimeOffset.MinValue)),
                CloseBehavior.Filled => Task.FromResult(new BrokerOrder(
                    $"close-{MarketCloseCount}", closeIntent, OrderStatus.Filled, closeIntent.Quantity, closeIntent.Price,
                    DateTimeOffset.MinValue, DateTimeOffset.MinValue)),
                CloseBehavior.Unavailable => throw new BrokerUnavailableException("OpenD 切断・成行は未発注（テスト）"),
                _ => throw new BrokerDispatchIndeterminateException("送信済みだが結果を確認できない（テスト）"),
            };
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(new BrokerOrder(
                orderId, new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                    BrokerProvider.MoomooSimulate, 10, 950m, PositionEffect.Close),
                StopStatus, StopStatus == OrderStatus.Filled ? 10 : 0, StopStatus == OrderStatus.Filled ? 950m : 0m,
                DateTimeOffset.MinValue, OrderStatusLifecycle.IsTerminal(StopStatus) ? DateTimeOffset.MinValue : null));

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Positions);
    }

    // 保護記録の引き直し（Find）だけを例外へ倒せるストア（原則 A の固定用）。他は内側へ委ねる。
    private sealed class FlakyFindStore(IProtectiveStopOrderStore inner) : IProtectiveStopOrderStore
    {
        public bool FailFind { get; set; }

        public int FindCalls { get; private set; }

        public ProtectiveStopOrder? Find(Guid entryDecisionId)
        {
            FindCalls++;
            return FailFind ? throw new InvalidOperationException("保護記録の照会に失敗（テスト）") : inner.Find(entryDecisionId);
        }

        public void Save(ProtectiveStopOrder stop) => inner.Save(stop);

        public IReadOnlyList<ProtectiveStopOrder> FindActive(int batchSize) => inner.FindActive(batchSize);

        public IReadOnlyList<ProtectiveStopOrder> FindActiveSoftwareStops(string symbol, Market market, TradeSide entrySide) =>
            inner.FindActiveSoftwareStops(symbol, market, entrySide);

        public IReadOnlyList<ProtectiveStopOrder> FindCompletedSoftwareStops(
            string symbol, Market market, TradeSide entrySide, int limit) =>
            inner.FindCompletedSoftwareStops(symbol, market, entrySide, limit);
    }

    private sealed record Harness(
        ProtectiveStopGuard Guard,
        GuardBroker Broker,
        FlakyFindStore Stops,
        InMemoryExecutedOrderStore Store,
        InMemoryOrderReservationStore Reservations,
        FakeClock Clock,
        Guid EntryDecisionId,
        CloseRejectionTracker Rejections,
        HeldCloseNotificationTracker Held)
    {
        public ProtectiveStopOrder Current => Stops.Find(EntryDecisionId)!;

        public Guid CloseDecisionId(int attempt) => ProtectiveStopIds.CloseDecisionId(EntryDecisionId, attempt);

        public Task<ProtectiveStopGuardResult> PatrolAsync() => Guard.RunOnceAsync(batchSize: 100);
    }

    private static Harness NewHarness()
    {
        var clock = new FakeClock();
        var broker = new GuardBroker();
        var stops = new FlakyFindStore(new InMemoryProtectiveStopOrderStore());
        var entryDecisionId = Guid.NewGuid();
        stops.Save(new ProtectiveStopOrder(
            entryDecisionId, Guid.NewGuid(), "stop-1", "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, Attempt: 1,
            ProtectiveStopState.Active, Start.AddMinutes(-5), Start.AddMinutes(-5)));
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        // 本番と同じく記憶は外から渡す（singleton。ガードは巡回ごとに作られる）。
        var rejections = new CloseRejectionTracker();
        var held = new HeldCloseNotificationTracker();
        var guard = new ProtectiveStopGuard(
            broker, broker, stops, store, reservations, clock,
            heldCloseNotifications: held, closeRejections: rejections);
        return new Harness(guard, broker, stops, store, reservations, clock, entryDecisionId, rejections, held);
    }

    // 建玉の減少は 2 巡回連続の観測で確定する（IADR-0344 追記(5)・(7)）。確定するまで巡回し、完了させた巡回の結果を返す。
    private static async Task<ProtectiveStopGuardResult> PatrolUntilCompletedAsync(Harness h)
    {
        for (var i = 0; i < 5; i++)
        {
            h.Clock.UtcNow += TimeSpan.FromSeconds(30);
            var result = await h.PatrolAsync();
            if (h.Current.State == ProtectiveStopState.Completed)
                return result;
        }

        throw new InvalidOperationException("5 巡回しても記録が完了しなかった（前提の組み立ての誤り）");
    }

    public enum CompletionPath
    {
        /// <summary>逆指値は滞留中（受理のまま）だが建玉が消えた → 残存逆指値を取り消して完了。</summary>
        PositionGoneStopPending,

        /// <summary>逆指値がブローカー側で約定した（損切り成立）→ 完了。</summary>
        StopFilled,

        /// <summary>逆指値は失効し、建玉も無い → 完了。</summary>
        LapsedAndNoPosition,
    }

    // 🔴 T-10-752, #938 F4: 成行が拒否された（数え 1）あと、利用者が証券会社の画面で手仕舞った等で**ガード内の完了の 3 経路**の
    // どれかを通っても、その記録の拒否の記憶はプロセスに残らない。**完了させたその巡回のうちに**捨てる（次の巡回の引き直しに頼らない）。
    [Theory]
    [InlineData(CompletionPath.PositionGoneStopPending)]
    [InlineData(CompletionPath.StopFilled)]
    [InlineData(CompletionPath.LapsedAndNoPosition)]
    public async Task 拒否の後に記録がガード内の経路で完了したら_拒否の記憶は残らない_否定形(CompletionPath path)
    {
        var h = NewHarness();
        var rejected = await h.PatrolAsync();
        rejected.CloseRejected.Should().Be(1, "前提: 成行が確認できた拒否で返った");
        h.Rejections.Count(h.EntryDecisionId).Should().Be(1);

        switch (path)
        {
            case CompletionPath.PositionGoneStopPending:
                h.Broker.StopStatus = OrderStatus.Accepted;
                h.Broker.Positions = [];
                break;
            case CompletionPath.StopFilled:
                h.Broker.StopStatus = OrderStatus.Filled;
                break;
            case CompletionPath.LapsedAndNoPosition:
                h.Broker.Positions = [];
                break;
        }

        var completing = await PatrolUntilCompletedAsync(h);

        completing.Completed.Should().Be(1);
        h.Broker.MarketCloseCount.Should().Be(1, "完了の経路では成行を送らない（前提）");
        h.Rejections.Count(h.EntryDecisionId).Should().Be(0, "完了した記録の拒否の数えを残さない");
        h.Rejections.IsRenotifyDue(h.EntryDecisionId, h.Clock.UtcNow).Should().BeTrue("通知時刻の記憶も残さない");
        h.Rejections.TrackedEntryDecisionIds.Should().BeEmpty("プロセスの寿命のあいだ辞書が増え続けない");
    }

    // 🔴 T-10-753, #938（F4 の同型・HeldCloseNotificationTracker）: 据え置き（届いたか不明）の記憶も、記録が完了したら残らない。
    [Fact]
    public async Task 据え置きの後に建玉が消えて記録が完了したら_据え置きの記憶は残らない_否定形()
    {
        var h = NewHarness();
        h.Broker.Close = CloseBehavior.Indeterminate;
        await h.PatrolAsync();
        h.Held.TrackedEntryDecisionIds.Should().Equal([h.EntryDecisionId], "前提: 据え置きを通知した");

        h.Broker.Positions = []; // 送った成行が約定した・利用者が手で手仕舞った
        await PatrolUntilCompletedAsync(h);

        h.Held.TrackedEntryDecisionIds.Should().BeEmpty();
        h.Held.IsDue(h.CloseDecisionId(2), h.Clock.UtcNow).Should().BeTrue("完了した記録の据え置きの通知時刻を残さない");
    }

    // 🔴 T-10-753（続き）: 据え置いた手仕舞いレグが突合で**拒否と確定**した（記録が残る）→ ガードは RejectedClose で試行を進める。
    // 古い CloseDecisionId は以後どの巡回にも引かれないので、その記憶は残さない。記録は Active のまま（拒否は完了ではない）。
    [Fact]
    public async Task 据え置いた手仕舞いが拒否と確定したら_古いレグの据え置きの記憶は残らない_否定形()
    {
        var h = NewHarness();
        h.Broker.Close = CloseBehavior.Indeterminate;
        await h.PatrolAsync();
        var heldLeg = h.CloseDecisionId(2);
        h.Held.IsDue(heldLeg, h.Clock.UtcNow).Should().BeFalse("前提: 据え置きを通知した");

        // 突合（IADR-0074）が、据え置いたレグを「発注済み・拒否」と確定した。
        h.Store.Save(new ExecutionRecord(
            heldLeg, "close-probed", "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, 10, 950m, 0, 0m, OrderStatus.Rejected, 0m, h.Clock.UtcNow));
        h.Reservations.MarkCompleted(heldLeg, "close-probed", h.Clock.UtcNow);
        h.Broker.Close = CloseBehavior.Rejected;
        h.Clock.UtcNow += TimeSpan.FromSeconds(30);

        var result = await h.PatrolAsync();

        result.CloseRejected.Should().Be(1);
        h.Current.State.Should().Be(ProtectiveStopState.Active, "拒否は完了ではない（IADR-0369 決定 1）");
        h.Held.TrackedEntryDecisionIds.Should().BeEmpty("拒否と確定したレグの据え置きの記憶を残さない");
        h.Rejections.Count(h.EntryDecisionId).Should().Be(1, "拒否の数えは残す（記録は Active のまま）");
    }

    // 🔴 T-10-754, #938 F4: **ガードの外**（乖離の取り込み・ProtectiveStopDriftAdopter。IADR-0370）で記録が完了した。
    // ガードは完了させていないので MarkCompleted を通らない。次の巡回が、記憶に載っている記録を引き直して捨てる。
    // 巡回対象（Active）が 0 件でも捨てる——最後の Active 行が外で完了したときこそ、以後の巡回は早期に戻る。
    [Fact]
    public async Task ガードの外で記録が完了したら_次の巡回で両方の記憶が消える_巡回対象が0件でも()
    {
        var h = NewHarness();
        await h.PatrolAsync(); // 拒否（数え 1・試行 2 へ）
        h.Broker.Close = CloseBehavior.Indeterminate;
        h.Clock.UtcNow += TimeSpan.FromSeconds(30);
        await h.PatrolAsync(); // 据え置き（試行 3 のレグ）
        h.Rejections.TrackedEntryDecisionIds.Should().Equal([h.EntryDecisionId], "前提");
        h.Held.TrackedEntryDecisionIds.Should().Equal([h.EntryDecisionId], "前提");

        // 乖離の取り込みの追随が残保護を 0 にして終端化した（ProtectiveStopDriftAdopter.ReduceBooks と同じ形）。
        h.Stops.Save(h.Current with { RemainingProtected = 0, State = ProtectiveStopState.Completed });
        // 記録そのものが無い記憶（行が無い＝照会は成功して「無い」と答えた）も捨てる。
        var orphan = Guid.NewGuid();
        h.Rejections.Record(orphan);
        h.Clock.UtcNow += TimeSpan.FromSeconds(30);

        var result = await h.PatrolAsync();

        result.Scanned.Should().Be(0, "前提: 巡回対象が 0 件（早期に戻る巡回）");
        h.Rejections.TrackedEntryDecisionIds.Should().BeEmpty();
        h.Held.TrackedEntryDecisionIds.Should().BeEmpty();
        h.Rejections.Count(orphan).Should().Be(0);
    }

    // 🔴 T-10-755, #938, 原則 A: 記録を**引き直せない**（照会が例外）のは「分からない」であって「無い」ではない。記憶を捨てない。
    // 捨てると拒否の数えが 0 へ戻り、まだ Active かもしれない記録へ成行を最大 3 本撃ち直す側へ倒れる。
    // 引き直せるようになれば、その巡回で捨てる。
    [Fact]
    public async Task 記録を引き直せないときは記憶を捨てない_原則A()
    {
        var h = NewHarness();
        await h.PatrolAsync();
        h.Stops.Save(h.Current with { RemainingProtected = 0, State = ProtectiveStopState.Completed });
        h.Stops.FailFind = true;
        h.Clock.UtcNow += TimeSpan.FromSeconds(30);

        var unknown = await h.PatrolAsync();

        unknown.Scanned.Should().Be(0);
        h.Stops.FindCalls.Should().BeGreaterThan(0, "前提: 引き直しを試みた");
        h.Rejections.Count(h.EntryDecisionId).Should().Be(1, "不明を「無い」として扱わない");

        h.Stops.FailFind = false;
        h.Clock.UtcNow += TimeSpan.FromSeconds(30);
        await h.PatrolAsync();

        h.Rejections.Count(h.EntryDecisionId).Should().Be(0, "引き直せたら捨てる");
    }

    // T-10-755 の対（変えない側）: 記録が Active のあいだは、巡回の冒頭の引き直しで記憶を捨てない（撃ち直しの上限が効き続ける）。
    [Fact]
    public async Task 記録がActiveのあいだは引き直しで記憶を捨てず_拒否は上限で止まる()
    {
        var h = NewHarness();
        for (var i = 0; i < 5; i++)
        {
            await h.PatrolAsync();
            h.Clock.UtcNow += TimeSpan.FromSeconds(30);
        }

        h.Rejections.Count(h.EntryDecisionId).Should().Be(ProtectiveStopGuard.MaxConfirmedCloseRejections);
        h.Broker.MarketCloseCount.Should().Be(3, "Active の記録の数えを巡回ごとに捨てると、成行を撃ち続ける");
    }

    // 🔴 T-10-756, #938 F5: 成行手仕舞いも**確実に未発注**で失敗した（Remediation=None）巡回を「手仕舞い」（ClosedOut）に数えない。
    // 建玉は残り、記録は Active のまま次の巡回で撃ち直す。通知（None・Critical）はこれまでどおり出る。
    [Fact]
    public async Task 成行手仕舞いが確実に未発注で失敗したら_手仕舞いに数えず手仕舞い失敗として数える_否定形()
    {
        var h = NewHarness();
        h.Broker.Close = CloseBehavior.Unavailable;

        var result = await h.PatrolAsync();

        result.ClosedOut.Should().Be(0, "手仕舞えていない建玉を「手仕舞い」の件数に入れない");
        result.CloseFailed.Should().Be(1);
        result.CloseRejected.Should().Be(0);
        result.Unknown.Should().Be(0);
        result.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle(e =>
            e.Remediation == ProtectiveStopRemediation.None && e.Cause == ProtectiveStopLossCause.LapsedInFlight);
        h.Current.State.Should().Be(ProtectiveStopState.Active, "次の巡回で撃ち直す");
        h.Reservations.Find(h.CloseDecisionId(2)).Should().BeNull("確実に未発注なので予約は解放されている");
    }

    // T-10-756 の対（変えない側）: 受理された手仕舞いは ClosedOut のまま。
    [Fact]
    public async Task 成行手仕舞いが受理されたら_手仕舞いに数える()
    {
        var h = NewHarness();
        h.Broker.Close = CloseBehavior.Filled;

        var result = await h.PatrolAsync();

        result.ClosedOut.Should().Be(1);
        result.CloseFailed.Should().Be(0);
    }
}
