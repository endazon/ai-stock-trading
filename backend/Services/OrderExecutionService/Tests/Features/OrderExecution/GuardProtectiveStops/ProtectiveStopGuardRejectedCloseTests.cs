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

// 🔴 T-10-634〜T-10-638, FR-10, FR-11, UC-02, UC-06, #857, IADR-0369, IADR-0210 決定4:
// **保護喪失時の成行手仕舞いが「確認できた拒否」で返っても、手仕舞い済みとして扱わない。**
//
// 是正前（#851 の前後で同一）: `PlaceMarketOrderAsync` が返りさえすれば `closeOrder.Status` を見ずに
// `ExecutionRecord` を保存し、記録を `Completed` にして `PositionClosed` を発行していた。帰結は 3 つ。
//   1. 建玉は残っているのに通知は「建玉を成行で手仕舞いました」と言う（Critical だが内容が逆）。
//   2. 記録が Completed になるため**以後の巡回がこの建玉を見ない**＝逆指値なしの建玉が黙って残る。
//   3. 取引台帳に手仕舞いレグの承認行が足され、送られてもいない決済が 30 分ぶん在庫を押さえる。
//
// 本クラスは「拒否されたら完了しない・巡回に残る・台帳へレグを運ばない・撃ち直しは上限で止まるが通知は止まらない」
// を固定する。**時計は注入**であり実時間を待たない（#885 / #900 / #901）。
public class ProtectiveStopGuardRejectedCloseTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 23, 7, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    public enum CloseBehavior
    {
        /// <summary>🔴 **確認できた拒否**（終端が返る。例外ではない）。</summary>
        Rejected,

        /// <summary>受理されて全量約定。</summary>
        Filled,
    }

    // 逆指値の再発注は既定で「確認できた拒否」を返す（→ 成行手仕舞いへ落ちる）。成行の挙動だけを注入する。
    private sealed class GuardBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public CloseBehavior Close { get; set; } = CloseBehavior.Rejected;

        /// <summary>逆指値の再発注が受理されるか（既定は拒否＝成行手仕舞いへ落ちる）。</summary>
        public bool AcceptStop { get; set; }

        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [new("AAPL", Market.UnitedStates, 10, 1_000m)];

        public int StopPlaceCount { get; private set; }
        public int MarketCloseCount { get; private set; }
        public List<Guid> MarketCloseDecisionIds { get; } = [];

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("ガードは通常発注を行わない");

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default)
        {
            StopPlaceCount++;
            return Task.FromResult(new BrokerOrder(
                $"stop-re-{StopPlaceCount}", closeIntent,
                AcceptStop ? OrderStatus.Accepted : OrderStatus.Rejected, 0, 0m, DateTimeOffset.MinValue, null));
        }

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCloseCount++;
            MarketCloseDecisionIds.Add(decisionId);
            return Task.FromResult(Close == CloseBehavior.Rejected
                ? new BrokerOrder($"close-{MarketCloseCount}", closeIntent, OrderStatus.Rejected, 0, 0m,
                    DateTimeOffset.MinValue, DateTimeOffset.MinValue)
                : new BrokerOrder($"close-{MarketCloseCount}", closeIntent, OrderStatus.Filled,
                    closeIntent.Quantity, closeIntent.Price, DateTimeOffset.MinValue, DateTimeOffset.MinValue));
        }

        // 元の逆指値は失効している（Cancelled）＝毎巡回 ReplaceOrClose へ入る。
        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(new BrokerOrder(
                orderId, new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                    BrokerProvider.MoomooSimulate, 10, 950m, PositionEffect.Close),
                OrderStatus.Cancelled, 0, 0m, DateTimeOffset.MinValue, DateTimeOffset.MinValue));

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Positions);
    }

    private sealed record Harness(
        ProtectiveStopGuard Guard,
        GuardBroker Broker,
        InMemoryProtectiveStopOrderStore Stops,
        InMemoryExecutedOrderStore Store,
        InMemoryOrderReservationStore Reservations,
        FakeClock Clock,
        ProtectiveStopOrder Stop)
    {
        /// <summary>試行 <paramref name="attempt"/> の成行手仕舞いレグの決定的な DecisionId。</summary>
        public Guid CloseDecisionId(int attempt) => ProtectiveStopIds.CloseDecisionId(Stop.EntryDecisionId, attempt);

        public ProtectiveStopOrder Current => Stops.Find(Stop.EntryDecisionId)!;
    }

    private static Harness NewHarness()
    {
        var clock = new FakeClock();
        var broker = new GuardBroker();
        var stops = new InMemoryProtectiveStopOrderStore();
        var stop = new ProtectiveStopOrder(
            Guid.NewGuid(), Guid.NewGuid(), "stop-1", "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, Attempt: 1,
            ProtectiveStopState.Active, Start.AddMinutes(-5), Start.AddMinutes(-5));
        stops.Save(stop);
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var guard = new ProtectiveStopGuard(broker, broker, stops, store, reservations, clock);
        return new Harness(guard, broker, stops, store, reservations, clock, stop);
    }

    // ---- 🔴 本 issue そのもの ----

    // T-10-634: 拒否された手仕舞いで記録を閉じない（＝建玉が巡回対象から外れない）。
    [Fact]
    public async Task 成行手仕舞いが確認できた拒否なら_記録はActiveのままで巡回対象から外れない_否定形()
    {
        var h = NewHarness();

        var first = await h.Guard.RunOnceAsync(10);

        first.ClosedOut.Should().Be(0, "手仕舞えていない");
        first.Completed.Should().Be(0);
        first.Unknown.Should().Be(0, "確認できた拒否は『不明』ではない");
        first.CloseRejected.Should().Be(1);
        h.Current.State.Should().Be(ProtectiveStopState.Active,
            "完了させると逆指値なしの建玉が巡回対象から外れ、無音で残る（本 issue の中心）");
        h.Current.Attempt.Should().Be(2, "試行番号を進めて、次の巡回は新しい CloseDecisionId で評価する");
        h.Current.RemainingProtected.Should().Be(h.Stop.RemainingProtected, "減ったのは建玉ではない（帳簿を動かさない）");

        // 次の巡回でもこの建玉は評価される（撃ち直しの上限までは撃ち直す）。
        var second = await h.Guard.RunOnceAsync(10);
        second.Scanned.Should().Be(1);
        // 撃ち直しは試行ごとに別の DecisionId で送る（同じ ID の予約に当たって据え置かない）。
        h.Broker.MarketCloseDecisionIds.Should().Equal(h.CloseDecisionId(2), h.CloseDecisionId(3));
    }

    // T-10-635: 通知は CloseRejected。手仕舞いレグ（CloseIntent）を運ばない＝台帳が在庫を押さえない。
    [Fact]
    public async Task 拒否の通知は手仕舞い済みと言わず_台帳へ手仕舞いレグを運ばない()
    {
        var h = NewHarness();

        var result = await h.Guard.RunOnceAsync(10);

        var lost = result.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle().Which;
        lost.Remediation.Should().Be(ProtectiveStopRemediation.CloseRejected);
        lost.Cause.Should().Be(ProtectiveStopLossCause.LapsedInFlight);
        lost.Quantity.Should().Be(10);
        // 🔴 CloseIntent を運ばない＝ProtectiveStopCoverageLostLedgerHandler は承認行を足さない。
        lost.CloseIntent.Should().BeNull("送った成行は生きていない。処理中の決済として在庫を押さえさせない");
        lost.CloseDecisionId.Should().Be(h.CloseDecisionId(2), "拒否された発注記録との相関のために ID は残す");

        // 拒否は不明ではない: 予約は確定し、発注記録（拒否の一次証跡）も残る。
        h.Reservations.Find(h.CloseDecisionId(2))!.State.Should().Be(OrderDispatchState.Completed);
        h.Store.FindByDecisionId(h.CloseDecisionId(2))!.Status.Should().Be(OrderStatus.Rejected);
    }

    // T-10-636: 撃ち直しの上限と、上限に達した後も止まらない通知。
    [Fact]
    public async Task 拒否が続いても成行は上限3回で止まり_それでもCriticalは1時間ごとに出続ける()
    {
        var h = NewHarness();

        for (var i = 0; i < 5; i++)
            await h.Guard.RunOnceAsync(10);

        h.Broker.MarketCloseCount.Should().Be(ProtectiveStopGuard.MaxConfirmedCloseRejections,
            "同じ理由で拒否され続ける成行を 30 秒ごとに送り続けない");
        h.Current.State.Should().Be(ProtectiveStopState.Active, "上限に達しても記録は閉じない（閉じると無音になる）");

        // 上限に達した後の巡回は成行を送らず、同じ時刻のあいだは通知も重ねない。
        var quiet = await h.Guard.RunOnceAsync(10);
        quiet.CloseRejected.Should().Be(1);
        quiet.Events.Should().BeEmpty("30 秒ごとに Critical を重ねない");
        h.Broker.MarketCloseCount.Should().Be(ProtectiveStopGuard.MaxConfirmedCloseRejections);

        // 1 時間たてば、送らないまま改めて知らせる（無保護の建玉を無期限に黙らせない）。
        h.Clock.Advance(CloseRejectionTracker.RenotifyInterval);
        var renotified = await h.Guard.RunOnceAsync(10);

        var lost = renotified.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle().Which;
        lost.Remediation.Should().Be(ProtectiveStopRemediation.CloseRejected);
        lost.CloseDecisionId.Should().BeNull("この巡回では手仕舞いを 1 本も送っていない（送っていない ID を載せない）");
        h.Broker.MarketCloseCount.Should().Be(ProtectiveStopGuard.MaxConfirmedCloseRejections,
            "再通知は通知であって発注ではない");
    }

    // T-10-637: 記録済みの手仕舞いレグが終端（拒否）でも完了させない（送信後に行の更新だけが失われた窓）。
    [Fact]
    public async Task 記録済みの手仕舞いレグが拒否終端なら_記録を根拠に完了させない_否定形()
    {
        var h = NewHarness();
        var closeDecisionId = h.CloseDecisionId(2);

        // 送信は済み、発注記録は残ったが、保護記録の更新だけが失われた（Attempt は 1 のまま）。
        h.Store.Save(new ExecutionRecord(
            closeDecisionId, "close-1", "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, 10, 950m, 0, 0m, OrderStatus.Rejected, 0m, Start));
        h.Reservations.TryReserve(closeDecisionId, Start);
        h.Reservations.MarkCompleted(closeDecisionId, "close-1", Start);

        var result = await h.Guard.RunOnceAsync(10);

        h.Broker.MarketCloseCount.Should().Be(0, "記録がある試行の成行を重ねて送らない");
        result.ClosedOut.Should().Be(0);
        result.CloseRejected.Should().Be(1);
        h.Current.State.Should().Be(ProtectiveStopState.Active,
            "『記録があるから手仕舞い済み』は、拒否の記録では成り立たない");
        result.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle()
            .Which.Remediation.Should().Be(ProtectiveStopRemediation.CloseRejected);
    }

    // T-10-638: 保護を張り直せたら、撃ち直しの数えは 0 へ戻る（次に失効したらまた 3 回試せる）。
    [Fact]
    public async Task 逆指値を張り直せたら撃ち直しの数えが戻る()
    {
        var h = NewHarness();

        await h.Guard.RunOnceAsync(10);
        await h.Guard.RunOnceAsync(10);
        h.Broker.MarketCloseCount.Should().Be(2);

        // 逆指値の再発注が通った＝保護が戻った（成行を撃ち直す理由が消えた）。
        h.Broker.AcceptStop = true;
        var replaced = await h.Guard.RunOnceAsync(10);
        replaced.Replaced.Should().Be(1);
        replaced.Events.OfType<ProtectiveStopPlaced>().Should().ContainSingle();

        // 再び失効した（逆指値は Cancelled が返り続ける）。数えが戻っているので、また 3 回まで撃てる。
        h.Broker.AcceptStop = false;
        for (var i = 0; i < 4; i++)
            await h.Guard.RunOnceAsync(10);

        h.Broker.MarketCloseCount.Should().Be(2 + ProtectiveStopGuard.MaxConfirmedCloseRejections);
    }

    // ---- 変えない側 ----

    [Fact]
    public async Task 手仕舞いが約定したら従来どおり完了し手仕舞いレグを運ぶ()
    {
        var h = NewHarness();
        h.Broker.Close = CloseBehavior.Filled;

        var result = await h.Guard.RunOnceAsync(10);

        result.ClosedOut.Should().Be(1);
        result.CloseRejected.Should().Be(0);
        h.Current.State.Should().Be(ProtectiveStopState.Completed);
        var lost = result.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle().Which;
        lost.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed);
        lost.CloseIntent.Should().NotBeNull();
    }
}
