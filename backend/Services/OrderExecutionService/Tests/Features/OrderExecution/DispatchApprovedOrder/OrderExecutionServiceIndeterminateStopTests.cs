using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;
using Behavior = OrderExecutionService.Tests.StopLegScriptedBroker.StopBehavior;

namespace OrderExecutionService.Tests;

// 🔴 FR-10, FR-05, UC-02, #853, IADR-0210（2026-09-25 追記）, IADR-0428 決定1・決定3:
// **エントリーと同時に送った保護逆指値が「届いたか不明」なら、エントリーの取消も成行手仕舞いもせず据え置く**（オーナー裁定 2026-09-25）。
// 従来は例外の種類を見ずに「未受理」と同じ分岐（取消・成行）へ落ち、逆指値が実は生きていれば建玉だけが消えて逆指値が孤立した
// （発火で反対方向の建玉＝ショート）。確実に未発注（接続確立の失敗・確認できた拒否）だけが従来どおり建玉を持たない側へ進む。
public class OrderExecutionServiceIndeterminateStopTests
{
    private static readonly DateTimeOffset Now = StopLegScriptedBroker.Now;

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private static OrderIntent Intent(int qty = 10) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, qty, 1_000m,
            PositionEffect.Open, StopLossPrice: 950m, FxRateToBase: 1m);

    private static OrderApproved Approved(
        StopLossExecutionMethod method = StopLossExecutionMethod.BrokerStopOrder) =>
        new(Guid.NewGuid(), Intent(), 10, Now, StopLossMethod: method);

    private sealed record Harness(
        AppSvc Service,
        StopLegScriptedBroker Broker,
        InMemoryExecutedOrderStore Store,
        InMemoryProtectiveStopOrderStore Stops,
        InMemoryOrderReservationStore Reservations);

    private static Harness NewHarness(StopLegScriptedBroker broker)
    {
        var store = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var service = new AppSvc(broker, store, reservations, new FakeClock(), stops, brokerPositions: broker);
        return new Harness(service, broker, store, stops, reservations);
    }

    // ---- 🔴 T-10-1060: 届いたか不明なら取消も成行もせず据え置く ----

    [Theory]
    [InlineData(Behavior.Indeterminate, OrderStatus.Filled, 10)]
    [InlineData(Behavior.Indeterminate, OrderStatus.Accepted, 0)]
    [InlineData(Behavior.Unclassified, OrderStatus.Filled, 10)]
    [InlineData(Behavior.Unclassified, OrderStatus.PartiallyFilled, 4)]
    public async Task 逆指値の送信結果が不明ならエントリーを取り消さず成行も送らず据え置く_否定形(
        Behavior stop, OrderStatus entryStatus, int filled)
    {
        var h = NewHarness(new StopLegScriptedBroker { Stop = stop, EntryStatus = entryStatus, EntryFilled = filled });
        var approved = Approved();
        var leg = ProtectiveStopIds.StopDecisionId(approved.DecisionId, attempt: 1);

        var result = await h.Service.ExecuteAsync(approved);

        h.Broker.StopPlaceCount.Should().Be(1);
        h.Broker.CancelCount.Should().Be(0, "逆指値が生きていれば、エントリーを取り消すと逆指値が孤立する");
        h.Broker.MarketCloseCount.Should().Be(0, "逆指値が生きていれば、成行で建玉を落とすと逆指値だけが残り反対建玉を生む");
        result.Executed!.Status.Should().Be(entryStatus, "エントリーの結果は従来どおり確定して発行する");
        result.StopPlaced.Should().BeNull("届いたか分からない逆指値を「張った」とは言わない");

        var held = result.CoverageLost!;
        held.Remediation.Should().Be(ProtectiveStopRemediation.StopDispatchIndeterminate);
        held.Cause.Should().Be(ProtectiveStopLossCause.RejectedAtEntry);
        held.CloseDecisionId.Should().Be(leg, "運ぶのは逆指値レグ（台帳が逆指値の承認行として押さえる）");
        held.CloseIntent!.Side.Should().Be(TradeSide.Sell);
        held.CloseIntent.Quantity.Should().Be(10);

        h.Reservations.Find(leg)!.State.Should().Be(OrderDispatchState.Reserved, "予約は解放も確定もしない（同じレグを送り直さない）");
        h.Store.FindByDecisionId(leg).Should().BeNull("実在しない注文 ID の記録を作らない");
        var row = h.Stops.Find(approved.DecisionId)!;
        row.State.Should().Be(ProtectiveStopState.Active, "常駐ガードが巡回する（送信結果待ち）");
        row.IsStopDispatchPending.Should().BeTrue();
        row.StopDecisionId.Should().Be(leg);
        row.Attempt.Should().Be(1);
    }

    // ---- T-10-1061: 確実に未発注は従来どおり（予約は解放） ----

    [Theory]
    [InlineData(Behavior.Unavailable, OrderStatus.Filled, 10, ProtectiveStopRemediation.PositionClosed)]
    [InlineData(Behavior.Unavailable, OrderStatus.Accepted, 0, ProtectiveStopRemediation.EntryCancelled)]
    [InlineData(Behavior.Reject, OrderStatus.Filled, 10, ProtectiveStopRemediation.PositionClosed)]
    [InlineData(Behavior.Reject, OrderStatus.Accepted, 0, ProtectiveStopRemediation.EntryCancelled)]
    public async Task 確実に未発注の逆指値は従来どおり建玉を持たず予約を解放する(
        Behavior stop, OrderStatus entryStatus, int filled, ProtectiveStopRemediation expected)
    {
        var h = NewHarness(new StopLegScriptedBroker { Stop = stop, EntryStatus = entryStatus, EntryFilled = filled });
        var approved = Approved();

        var result = await h.Service.ExecuteAsync(approved);

        result.CoverageLost!.Remediation.Should().Be(expected);
        h.Reservations.Find(ProtectiveStopIds.StopDecisionId(approved.DecisionId, attempt: 1))
            .Should().BeNull("確実に未発注の逆指値の予約は解放する");
        h.Stops.Find(approved.DecisionId)!.State.Should().Be(
            ProtectiveStopState.Completed, "承認時の保護の文脈は閉じる（巡回の対象に入らない）");
        h.Stops.FindActive(100).Should().BeEmpty();
    }

    // ---- T-10-1062: 受理は 3 相で確定する ----

    [Fact]
    public async Task 受理された逆指値は記録を保存してから予約を注文IDつきで確定する()
    {
        var h = NewHarness(new StopLegScriptedBroker { Stop = Behavior.Accept });
        var approved = Approved();
        var leg = ProtectiveStopIds.StopDecisionId(approved.DecisionId, attempt: 1);

        var result = await h.Service.ExecuteAsync(approved);

        result.StopPlaced!.StopDecisionId.Should().Be(leg);
        var reservation = h.Reservations.Find(leg)!;
        reservation.State.Should().Be(OrderDispatchState.Completed);
        reservation.BrokerOrderId.Should().Be(result.StopPlaced.StopOrderId);
        h.Store.FindByDecisionId(leg)!.OrderId.Should().Be(result.StopPlaced.StopOrderId);
        h.Stops.Find(approved.DecisionId)!.StopOrderId.Should().Be(result.StopPlaced.StopOrderId);
    }

    [Fact]
    public async Task 逆指値の予約が既にあれば送らずに据え置く()
    {
        // T-10-1062（否定形）: 予約が取れない＝送信中か成否不明。重ねて送らない（取消・成行もしない）。
        var h = NewHarness(new StopLegScriptedBroker { Stop = Behavior.Accept });
        var approved = Approved();
        h.Reservations.TryReserve(ProtectiveStopIds.StopDecisionId(approved.DecisionId, attempt: 1), Now.AddMinutes(-1));

        var result = await h.Service.ExecuteAsync(approved);

        h.Broker.StopPlaceCount.Should().Be(0);
        h.Broker.CancelCount.Should().Be(0);
        h.Broker.MarketCloseCount.Should().Be(0);
        result.CoverageLost!.Remediation.Should().Be(ProtectiveStopRemediation.StopDispatchIndeterminate);
    }

    [Fact]
    public async Task 逆指値の予約そのものが落ちたら送らず巡回される送信結果待ちを残しガードが張り直す_否定形()
    {
        // T-10-1062（続き・PR #1005 監査 3）: 予約表が落ちた（DB 障害）。何もせずに戻ると、建玉は巡回されない AwaitingEntry の行だけを持ち、
        // 逆指値も取消も成行も通知も無いまま残る。送らずに保護記録を送信結果待ちで残し（ガードが巡回する）、保護喪失（None）で知らせる。
        var broker = new StopLegScriptedBroker { Stop = Behavior.Accept };
        var store = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var approved = Approved();
        var leg = ProtectiveStopIds.StopDecisionId(approved.DecisionId, attempt: 1);
        var service = new AppSvc(
            broker, store, new ThrowingReserveStore(reservations, leg), new FakeClock(), stops, brokerPositions: broker);

        var result = await service.ExecuteAsync(approved);

        broker.StopPlaceCount.Should().Be(0, "予約を記録できないまま逆指値を送らない");
        broker.CancelCount.Should().Be(0);
        broker.MarketCloseCount.Should().Be(0, "DB が不確かなまま予約なしで成行を重ねない");
        result.Executed.Should().NotBeNull();
        // PR #1005 再監査: 「未受理」「解消にも失敗」（None）ではなく、事実（予約できず送っていない）を運ぶ。
        var lost = result.CoverageLost!;
        lost.Remediation.Should().Be(ProtectiveStopRemediation.StopReservationFailed);
        lost.Cause.Should().Be(ProtectiveStopLossCause.RejectedAtEntry);
        lost.CloseDecisionId.Should().Be(leg, "送らなかった逆指値レグとの相関");
        lost.CloseIntent.Should().BeNull("生きている注文は無い（台帳に押さえさせない）");
        var row = stops.Find(approved.DecisionId)!;
        row.State.Should().Be(ProtectiveStopState.Active, "巡回されない AwaitingEntry のまま残さない");
        row.IsStopDispatchPending.Should().BeTrue();

        // 常駐ガードは「予約も記録も無い送信結果待ち」を未発注として扱い、次の試行の新しいレグで逆指値を張り直す。
        var guard = new OrderExecutionService.Features.OrderExecution.GuardProtectiveStops.ProtectiveStopGuard(
            broker, broker, stops, store, reservations, new FakeClock());
        var patrol = await guard.RunOnceAsync(batchSize: 10);

        patrol.Replaced.Should().Be(1);
        broker.StopDecisionIds.Should().Equal([ProtectiveStopIds.StopDecisionId(approved.DecisionId, attempt: 2)]);
    }

    // ---- S3（代替注文種別）でも同じ ----

    [Fact]
    public async Task S3の保護レグが届いたか不明でも据え置き試行の記録を拒否として残さない()
    {
        // T-10-1060（S3）: 結果の扱いは S0 と同じ。状態を持たない失敗を「拒否（Rejected）」として監査台帳へ残さない。
        var h = NewHarness(new StopLegScriptedBroker { Stop = Behavior.Indeterminate });
        var approved = Approved(StopLossExecutionMethod.AlternativeBrokerOrderType);

        var result = await h.Service.ExecuteAsync(approved);

        h.Broker.AlternativeStopCount.Should().Be(1);
        h.Broker.CancelCount.Should().Be(0);
        h.Broker.MarketCloseCount.Should().Be(0);
        result.StopAttempted.Should().BeNull("生きているかもしれない注文を「拒否された」と書かない");
        result.CoverageLost!.Remediation.Should().Be(ProtectiveStopRemediation.StopDispatchIndeterminate);
        h.Stops.Find(approved.DecisionId)!.IsStopDispatchPending.Should().BeTrue();
    }

    // ---- T-10-1069: 承認時の保護の文脈の事前記録 ----

    [Theory]
    [InlineData(StopLossExecutionMethod.BrokerStopOrder, StopLossExecutionMethod.BrokerStopOrder)]
    [InlineData(StopLossExecutionMethod.AlternativeBrokerOrderType, StopLossExecutionMethod.AlternativeBrokerOrderType)]
    public async Task S0とS3はエントリーを送る前に承認時の保護の文脈を巡回対象外で残す(
        StopLossExecutionMethod method, StopLossExecutionMethod expectedMechanism)
    {
        var stops = new InMemoryProtectiveStopOrderStore();
        var broker = new EntryObservingBroker(stops);
        var service = new AppSvc(
            broker, new InMemoryExecutedOrderStore(), new InMemoryOrderReservationStore(), new FakeClock(), stops,
            brokerPositions: broker);
        var approved = Approved(method);
        broker.EntryDecisionId = approved.DecisionId;

        await service.ExecuteAsync(approved);

        var seen = broker.SeenAtEntry!;
        seen.State.Should().Be(ProtectiveStopState.AwaitingEntry, "エントリーを送る時点で承認時の文脈が残っている");
        seen.Mechanism.Should().Be(expectedMechanism);
        seen.TriggerPrice.Should().Be(950m);
        seen.Quantity.Should().Be(10);
        broker.ActiveAtEntry.Should().Be(0, "巡回の対象（Active）ではない——ガード・約定追跡・純額の計算は見ない");
        stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.Active, "受理で保護記録 Active に上書きされる");
    }

    [Fact]
    public async Task エントリーが確実に未発注なら承認時の保護の文脈を閉じる()
    {
        var stops = new InMemoryProtectiveStopOrderStore();
        var broker = new EntryObservingBroker(stops) { EntryUnavailable = true };
        var service = new AppSvc(
            broker, new InMemoryExecutedOrderStore(), new InMemoryOrderReservationStore(), new FakeClock(), stops,
            brokerPositions: broker);
        var approved = Approved();

        var result = await service.ExecuteAsync(approved);

        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.BrokerUnavailable);
        stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    [Fact]
    public async Task エントリーの送信結果が不明なら承認時の保護の文脈は残る()
    {
        // 突合が後から発注済みと確定したとき、承認時の手法で保護レグを張るために使う（T-10-1070）。
        var stops = new InMemoryProtectiveStopOrderStore();
        var broker = new EntryObservingBroker(stops) { EntryIndeterminate = true };
        var service = new AppSvc(
            broker, new InMemoryExecutedOrderStore(), new InMemoryOrderReservationStore(), new FakeClock(), stops,
            brokerPositions: broker);
        var approved = Approved();

        var dispatch = async () => await service.ExecuteAsync(approved);

        await dispatch.Should().ThrowAsync<AiStockTrading.Shared.Contracts.Ports.BrokerDispatchIndeterminateException>();
        stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.AwaitingEntry);
        stops.FindActive(100).Should().BeEmpty();
    }

    [Theory]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.Cancelled)]
    public async Task エントリーが終端失敗なら承認時の保護の文脈を閉じる(OrderStatus entryStatus)
    {
        var h = NewHarness(new StopLegScriptedBroker { EntryStatus = entryStatus, EntryFilled = 0 });
        var approved = Approved();

        await h.Service.ExecuteAsync(approved);

        h.Broker.StopPlaceCount.Should().Be(0);
        h.Stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    [Fact]
    public async Task S2は保護記録を作らない_従来どおり()
    {
        // T-10-1069（否定形）: S2 は保護逆指値を持たない手法であり、事前記録も作らない（巡回の対象にもならない）。
        var h = NewHarness(new StopLegScriptedBroker());
        var approved = Approved(StopLossExecutionMethod.NoProtectiveStop);

        var result = await h.Service.ExecuteAsync(approved);

        result.StopWaived.Should().NotBeNull();
        h.Stops.Find(approved.DecisionId).Should().BeNull();
    }
}

// T-10-1069: エントリーを送る瞬間の保護記録を観測するブローカー（受理で返す逆指値は Accepted）。
internal sealed class EntryObservingBroker(InMemoryProtectiveStopOrderStore stops)
    : AiStockTrading.Shared.Contracts.Ports.IBrokerAdapter,
      AiStockTrading.Shared.Contracts.Ports.IProtectiveOrderBroker,
      AiStockTrading.Shared.Contracts.Ports.IAlternativeProtectiveOrderBroker,
      AiStockTrading.Shared.Contracts.Ports.IBrokerPositionSource
{
    public BrokerProvider Provider => BrokerProvider.MoomooSimulate;
    public bool EntryUnavailable { get; init; }
    public bool EntryIndeterminate { get; init; }
    public Guid EntryDecisionId { get; set; }
    public ProtectiveStopOrder? SeenAtEntry { get; private set; }
    public int ActiveAtEntry { get; private set; }

    public AlternativeProtectiveOrderType AlternativeProtectiveOrderType => AlternativeProtectiveOrderType.StopLimit;

    public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
    {
        SeenAtEntry = stops.Find(EntryDecisionId);
        ActiveAtEntry = stops.FindActive(100).Count;
        if (EntryUnavailable)
            throw new AiStockTrading.Shared.Contracts.Ports.BrokerUnavailableException("OpenD 切断（テスト）");
        if (EntryIndeterminate)
            throw new AiStockTrading.Shared.Contracts.Ports.BrokerDispatchIndeterminateException("結果不明（テスト）");
        return Task.FromResult(new BrokerOrder("entry-1", intent, OrderStatus.Filled, 10, 1_000m, StopLegScriptedBroker.Now,
            StopLegScriptedBroker.Now));
    }

    public Task<BrokerOrder> PlaceStopOrderAsync(
        OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
        Task.FromResult(new BrokerOrder("stop-1", closeIntent, OrderStatus.Accepted, 0, 0m, StopLegScriptedBroker.Now, null));

    public Task<AiStockTrading.Shared.Contracts.Ports.AlternativeProtectiveOrderPlacement> PlaceAlternativeStopOrderAsync(
        OrderIntent closeIntent, decimal triggerPrice, decimal entryReferencePrice, Guid decisionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AiStockTrading.Shared.Contracts.Ports.AlternativeProtectiveOrderPlacement(
            new BrokerOrder("stop-1", closeIntent, OrderStatus.Accepted, 0, 0m, StopLegScriptedBroker.Now, null),
            AlternativeProtectiveOrderType.StopLimit, null, null, "stop-1"));

    public Task<BrokerOrder> PlaceMarketOrderAsync(OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) =>
        throw new InvalidOperationException("本テストは成行を送らない");

    public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
        Task.FromResult<BrokerOrder?>(null);

    public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BrokerPositionSnapshot>?>([]);
}
