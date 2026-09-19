using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-409, FR-05, FR-10, FR-11, UC-06, #848, IADR-0117（2026-09-19 追記・改定 7）:
// **保護逆指値ガードの成行手仕舞いが「届いたか不明」で終わったとき、未発注と仮定して撃ち直さない。**
//
// PR #851 の 3 巡目監査（B4）の実測: 改定 6 が「届いたか不明」を終端 Rejected の**戻り値**から
// BrokerDispatchIndeterminateException（**例外**）へ変えた結果、ガードの一括 catch がそれを
// 「次の巡回で再試行してよい失敗」として受け、記録を Active のまま残していた。
//
//   indeterminate=False（改定 6 以前の形）: 1巡目=(Completed, 成行 1 回)  2巡目=(Completed, 成行 1 回)
//   indeterminate=True （改定 6 以後の形）: 1巡目=(Active,    成行 1 回)  2巡目=(Active,    成行 2 回)
//
// 巡回（既定 30 秒）ごとに全数量の成行が 1 本ずつ増える＝**二重決済でショート化**。
// 本クラスは「状態が Unknown になる」ではなく、**巡回を重ねても成行の送信回数が 1 回のまま**であることを固定する。
public class ProtectiveStopGuardIndeterminateCloseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 7, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    public enum CloseBehavior
    {
        /// <summary>送信は済んだが結果を確認できない（返信待ちのタイムアウト）。</summary>
        Indeterminate,

        /// <summary>分類できない例外（未発注と言い切れない）。</summary>
        Unclassified,

        /// <summary>接続確立の失敗＝確実に未発注。</summary>
        Unavailable,

        /// <summary>受理されて全量約定。</summary>
        Filled,
    }

    // 逆指値の再発注は常に「確認できた拒否」を返す（→ 成行手仕舞いへ落ちる）。成行の挙動だけを注入する。
    private sealed class GuardBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public CloseBehavior Close { get; set; } = CloseBehavior.Indeterminate;
        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [Long(10)];

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
                $"stop-re-{StopPlaceCount}", closeIntent, OrderStatus.Rejected, 0, 0m, Now, Now));
        }

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            // 🔴 数えるのは「送った回数」。例外で終わっても送信は済んでいる（Unavailable を除く）。
            MarketCloseCount++;
            MarketCloseDecisionIds.Add(decisionId);
            return Close switch
            {
                CloseBehavior.Indeterminate => throw new BrokerDispatchIndeterminateException(
                    "moomoo へ発注を送信しましたが結果を確認できませんでした（テスト）",
                    new TimeoutException("返信待ちタイムアウト（テスト）")),
                CloseBehavior.Unclassified => throw new InvalidOperationException("分類できない失敗（テスト）"),
                CloseBehavior.Unavailable => throw new BrokerUnavailableException("OpenD 切断（テスト）"),
                _ => Task.FromResult(new BrokerOrder(
                    $"close-{MarketCloseCount}", closeIntent, OrderStatus.Filled,
                    closeIntent.Quantity, closeIntent.Price, Now, Now)),
            };
        }

        // 元の逆指値は失効している（Cancelled）＝毎巡回 ReplaceOrClose へ入る。
        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(new BrokerOrder(
                orderId, new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                    BrokerProvider.MoomooSimulate, 10, 950m, PositionEffect.Close),
                OrderStatus.Cancelled, 0, 0m, Now, Now));

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Positions);
    }

    private static BrokerPositionSnapshot Long(int qty) => new("AAPL", Market.UnitedStates, qty, 1_000m);

    private static ProtectiveStopOrder ActiveStop() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "stop-1", "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, Attempt: 1,
            ProtectiveStopState.Active, Now.AddMinutes(-5), Now.AddMinutes(-5));

    private sealed record Harness(
        ProtectiveStopGuard Guard,
        GuardBroker Broker,
        InMemoryProtectiveStopOrderStore Stops,
        InMemoryExecutedOrderStore Store,
        InMemoryOrderReservationStore Reservations,
        ProtectiveStopOrder Stop)
    {
        /// <summary>この試行（Attempt + 1 ＝ 2）の成行手仕舞いレグの決定的な DecisionId。</summary>
        public Guid CloseDecisionId => ProtectiveStopIds.CloseDecisionId(Stop.EntryDecisionId, attempt: 2);
    }

    private static Harness NewHarness(CloseBehavior close)
    {
        var broker = new GuardBroker { Close = close };
        var stops = new InMemoryProtectiveStopOrderStore();
        var stop = ActiveStop();
        stops.Save(stop);
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var guard = new ProtectiveStopGuard(broker, broker, stops, store, reservations, new FakeClock());
        return new Harness(guard, broker, stops, store, reservations, stop);
    }

    // ---- 🔴 B4 そのもの: 巡回を重ねても成行を重ねない ----

    // 「届いたか不明」（改定 6 の型）と「分類できない例外」（未発注と言い切れない）は同じ側へ倒す。
    [Theory]
    [InlineData(CloseBehavior.Indeterminate)]
    [InlineData(CloseBehavior.Unclassified)]
    public async Task 成行手仕舞いが届いたか不明なら_3巡回まわしても成行の送信は1回のまま_否定形(CloseBehavior close)
    {
        var h = NewHarness(close);

        var first = await h.Guard.RunOnceAsync(10);
        var second = await h.Guard.RunOnceAsync(10);
        var third = await h.Guard.RunOnceAsync(10);

        // 🔴 結果の固定: 送った成行は 1 本だけ（是正前は 1 → 2 → 3 と巡回ごとに増えた）。
        h.Broker.MarketCloseCount.Should().Be(1, "届いたか不明の成行を、未発注と仮定して撃ち直してはならない（二重決済でショート化）");
        h.Broker.MarketCloseDecisionIds.Should().Equal(h.CloseDecisionId);

        // 逆指値の再発注も重ねない（成行が生きているかもしれない建玉へ張ると、約定後に建玉なき逆指値が残る）。
        h.Broker.StopPlaceCount.Should().Be(1, "成行の結果が不明のあいだは逆指値も重ねない");

        // 3 巡回とも据え置き（Unknown）。完了を主張しない。
        first.Unknown.Should().Be(1);
        second.Unknown.Should().Be(1);
        third.Unknown.Should().Be(1);
        first.ClosedOut.Should().Be(0);
        h.Stops.Find(h.Stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active, "手仕舞い済みを主張しない");
        h.Stops.Find(h.Stop.EntryDecisionId)!.Attempt.Should().Be(1, "試行番号を進めない＝同じ DecisionId の予約が効き続ける");

        // 「送ったかもしれない」は予約に残る（解放も確定もしない）。実在しない注文 ID の記録は作らない。
        var reservation = h.Reservations.Find(h.CloseDecisionId);
        reservation.Should().NotBeNull();
        reservation!.State.Should().Be(OrderDispatchState.Reserved);
        reservation.BrokerOrderId.Should().BeNull();
        h.Store.FindByDecisionId(h.CloseDecisionId).Should().BeNull("結果を知らないまま発注結果の記録を作らない");
    }

    [Fact]
    public async Task 届いたか不明は無音にしない_Criticalの通知を不明になった巡回で1回だけ出し_手仕舞いレグを運ぶ()
    {
        var h = NewHarness(CloseBehavior.Indeterminate);

        var first = await h.Guard.RunOnceAsync(10);
        var second = await h.Guard.RunOnceAsync(10);
        var third = await h.Guard.RunOnceAsync(10);

        var lost = first.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle().Which;
        lost.Remediation.Should().Be(ProtectiveStopRemediation.CloseDispatchIndeterminate,
            "None（解消に失敗）は手仕舞いレグを運ばず、読んだ人に手で成行を重ねさせる");
        lost.Cause.Should().Be(ProtectiveStopLossCause.LapsedInFlight);
        lost.Quantity.Should().Be(10);
        // 🔴 CloseIntent を運ぶ＝取引台帳が「処理中の決済」として在庫を押さえる（利用者の手仕舞い要求と二重にならない）。
        lost.CloseDecisionId.Should().Be(h.CloseDecisionId);
        lost.CloseIntent.Should().NotBeNull();
        lost.CloseIntent!.Quantity.Should().Be(10);
        lost.CloseIntent.Side.Should().Be(TradeSide.Sell);
        lost.CloseIntent.PositionEffect.Should().Be(PositionEffect.Close);

        // 以後の据え置きでは Critical を重ねない（30 秒ごとに同じ通知を積まない）。件数サマリとログには現れ続ける。
        second.Events.Should().BeEmpty();
        third.Events.Should().BeEmpty();
    }

    // ---- 変えない側: 確実に未発注なら撃ち直してよい ----

    [Fact]
    public async Task 確実に未発注_接続確立の失敗_なら予約を解放し_次の巡回で撃ち直す()
    {
        var h = NewHarness(CloseBehavior.Unavailable);

        var first = await h.Guard.RunOnceAsync(10);

        first.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle()
            .Which.Remediation.Should().Be(ProtectiveStopRemediation.None);
        h.Reservations.Find(h.CloseDecisionId).Should().BeNull("確実に未発注なので予約を解放する（二重発注の窓は無い）");
        h.Stops.Find(h.Stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);

        // OpenD が戻った次の巡回: 同じ DecisionId を再予約して撃ち直し、今度は手仕舞える。
        h.Broker.Close = CloseBehavior.Filled;
        var second = await h.Guard.RunOnceAsync(10);

        h.Broker.MarketCloseCount.Should().Be(2, "確実に未発注だった成行は撃ち直してよい");
        h.Broker.MarketCloseDecisionIds.Should().Equal(h.CloseDecisionId, h.CloseDecisionId);
        second.ClosedOut.Should().Be(1);
        second.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle()
            .Which.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed);
        h.Stops.Find(h.Stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    [Fact]
    public async Task 成行手仕舞いが成功したら予約を確定する()
    {
        var h = NewHarness(CloseBehavior.Filled);

        var result = await h.Guard.RunOnceAsync(10);

        result.ClosedOut.Should().Be(1);
        var reservation = h.Reservations.Find(h.CloseDecisionId);
        reservation!.State.Should().Be(OrderDispatchState.Completed);
        reservation.BrokerOrderId.Should().Be("close-1");
        h.Store.FindByDecisionId(h.CloseDecisionId)!.OrderId.Should().Be("close-1");
        h.Stops.Find(h.Stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // ---- 据え置いた予約の解決 ----

    [Fact]
    public async Task 突合が発注済みと解決したら_送らずに記録で完了させる()
    {
        var h = NewHarness(CloseBehavior.Indeterminate);
        await h.Guard.RunOnceAsync(10);
        h.Broker.MarketCloseCount.Should().Be(1);

        // 自動リコンサイル（有効な場合）が client order id で「発注済み」と解決した。
        var placed = new BrokerOrder(
            "9049618348733212748",
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 10, 950m, PositionEffect.Close),
            OrderStatus.Filled, 10, 949m, Now, Now);
        var reconciler = new OrderReservationReconciler(
            h.Reservations, h.Store, new StubProbe(ReservationProbeResult.Placed(placed)), h.Broker, new FakeClock());
        var reconciled = await reconciler.ReconcileAsync(Now.AddHours(1), 10);
        reconciled.Terminalized.Should().Be(1);

        var next = await h.Guard.RunOnceAsync(10);

        h.Broker.MarketCloseCount.Should().Be(1, "発注済みと判明した成行を送り直さない");
        h.Broker.StopPlaceCount.Should().Be(1, "手仕舞い済みの建玉へ逆指値を張らない");
        next.ClosedOut.Should().Be(1);
        h.Stops.Find(h.Stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        var lost = next.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle().Which;
        lost.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed);
        lost.CloseDecisionId.Should().Be(h.CloseDecisionId);
        h.Store.FindByDecisionId(h.CloseDecisionId)!.OrderId.Should().Be("9049618348733212748", "証券会社が採番した本物の注文 ID");
    }

    [Fact]
    public async Task 突合が未発注と解決したら_予約が解放され次の巡回で撃ち直せる()
    {
        var h = NewHarness(CloseBehavior.Indeterminate);
        await h.Guard.RunOnceAsync(10);

        var reconciler = new OrderReservationReconciler(
            h.Reservations, h.Store, new StubProbe(ReservationProbeResult.NotPlaced), h.Broker, new FakeClock());
        (await reconciler.ReconcileAsync(Now.AddHours(1), 10)).Released.Should().Be(1);

        h.Broker.Close = CloseBehavior.Filled;
        var next = await h.Guard.RunOnceAsync(10);

        h.Broker.MarketCloseCount.Should().Be(2, "未発注と**確認できた**ので撃ち直してよい");
        next.ClosedOut.Should().Be(1);
    }

    private sealed class StubProbe(ReservationProbeResult result) : IReservationBrokerProbe
    {
        public Task<ReservationProbeResult> ProbeAsync(
            OrderDispatchReservation reservation, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    // ---- 🔴 実アダプタを通した結線（B4 が実際に起きる経路そのもの） ----
    //
    // 上はブローカーが fake。ここでは**実際の MoomooBrokerAdapter** に「成行の送信後に返信が来ない」を食わせ、
    // ガードの出口まで同じ結論になることを固定する（改定 6 の例外化と、改定 7 の受け側が噛み合っていること）。
    private sealed class MarketCloseTimesOutTradeClient : IMoomooTradeClient
    {
        public int MarketSendCount { get; private set; }
        public int StopSendCount { get; private set; }

        public Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest request, CancellationToken ct = default)
        {
            if (request.Kind == MoomooOrderKind.Market)
            {
                MarketSendCount++; // 🔴 送信は済んでいる。だから「届いたか不明」であって「未発注」ではない。
                throw new TimeoutException("OpenD の返信待ちがタイムアウトしました（テスト）");
            }

            // 逆指値は証券会社が**確認できる形で**受理しなかった（SIMULATE は OrderType_Stop を受け付けない。#809）。
            StopSendCount++;
            throw new MoomooTradeRequestException("PlaceOrder", -1, "逆指値は受け付けられません（テスト）");
        }

        // 元の逆指値は失効している（Cancelled）。
        public Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<MoomooOrderResult?>(new MoomooOrderResult(orderId, MoomooOrderState.Cancelled, 0, 0m));

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<MoomooOrderSnapshot?> FindOrderByClientIdAsync(
            string clientOrderId, DateTimeOffset reservedAtUtc, CancellationToken ct = default) =>
            Task.FromResult<MoomooOrderSnapshot?>(null);

        // 建玉は残っている（ロング 10 株）。
        public Task<IReadOnlyList<MoomooPositionSnapshot>> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MoomooPositionSnapshot>>(
                [new MoomooPositionSnapshot("AAPL", MoomooMarket.UnitedStates, 10, 1_000m)]);

        public Task<MoomooAccountType?> GetAccountTypeAsync(CancellationToken ct = default) =>
            Task.FromResult<MoomooAccountType?>(MoomooAccountType.Margin);
    }

    [Fact]
    public async Task 実アダプタ経由_成行の送信後タイムアウトを_巡回ごとに撃ち直さない_否定形()
    {
        var client = new MarketCloseTimesOutTradeClient();
        var adapter = new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate);
        var stops = new InMemoryProtectiveStopOrderStore();
        var stop = ActiveStop();
        stops.Save(stop);
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var guard = new ProtectiveStopGuard(adapter, adapter, stops, store, reservations, new FakeClock());

        var first = await guard.RunOnceAsync(10);
        await guard.RunOnceAsync(10);
        await guard.RunOnceAsync(10);

        client.MarketSendCount.Should().Be(1, "OpenD へ送った成行は 1 本だけ（是正前は巡回ごとに 1 本ずつ増えた）");
        client.StopSendCount.Should().Be(1, "成行の結果が不明のあいだは逆指値も重ねない");
        first.Events.OfType<ProtectiveStopCoverageLost>().Should().ContainSingle()
            .Which.Remediation.Should().Be(ProtectiveStopRemediation.CloseDispatchIndeterminate);
        var closeDecisionId = ProtectiveStopIds.CloseDecisionId(stop.EntryDecisionId, attempt: 2);
        reservations.Find(closeDecisionId)!.State.Should().Be(OrderDispatchState.Reserved);
        store.FindByDecisionId(closeDecisionId).Should().BeNull("実在しない注文 ID の記録を作らない");
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }
}
