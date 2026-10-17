using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// 🔴 FR-05, FR-10, ADR-0040 決定1, ADR-0045, #1343, IADR-0550: **突合が「未発注」と確定し、解放の門が開いていたとき、
// 承認の経路の予約は削除せず見送り（Forgone）として閉じ、送る前に残した保護の記録（S1 の行・S0 / S3 の AwaitingEntry）も閉じる。**
//
// 是正前は予約行を消すだけで、S1 の行は発注の記録が無いまま「これから約定し得る」として永久に据え置かれ（ProtectiveStopGuard）、
// 同じ銘柄・同じ方向の別の手法の新規建てを見送り続けた（StopLossMethodCoexistenceGate。#856 の 2026-10-05 の AAPL）。
// さらに、消した予約の承認を _error から再投入すると、承認の古さを確かめずにそのまま送られた。
//
// 本試験は本物の発注執行（OrderExecutionAppService）で「送る前に結果が不明」（BeforeSend の故障注入と同じ形。
// 届いていないが BrokerDispatchIndeterminateException）を作り、本物のリコンサイラで解放してから、次の 3 点を固定する:
//   (1) 門による解放で S1 / AwaitingEntry が閉じ、予約は Forgone になる。
//   (2) 同じ銘柄・同じ方向の別の手法の新規建てが塞がれなくなる（門が閉じていれば塞がれたまま＝対照）。
//   (3) 元の承認を再投入しても発注しない（古い承認で送らない）。
// 殺す変異: 解放を Release（削除）へ戻す／保護の記録を閉じない／閉じる順序を予約の前にする（閉じる条件を緩めて到達済みの行を閉じる）。
public class UnplacedEntryReleaseForgoneTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 13, 44, 0, TimeSpan.Zero);

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = T0;
    }

    private sealed class StubProbe(ReservationProbeResult result) : IReservationBrokerProbe
    {
        public Task<ReservationProbeResult> ProbeAsync(
            OrderDispatchReservation reservation, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    // moomoo SIMULATE（S1 を選べる発注先）。IndeterminateEntry の間は新規建てを「届いたか不明」で落とす（送っていない）。
    private sealed class Broker
        : IBrokerAdapter, IProtectiveOrderBroker, IAlternativeProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public bool IndeterminateEntry { get; set; }

        public int Sent { get; private set; }

        public int StopsPlaced { get; private set; }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BrokerPositionSnapshot>?>([]);

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            if (IndeterminateEntry)
                throw new BrokerDispatchIndeterminateException("故障注入（BeforeSend 相当・テスト）");

            Sent++;
            return Task.FromResult(new BrokerOrder($"order-{Sent}", intent, OrderStatus.Accepted, 0, 0m, T0, null));
        }

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default)
        {
            StopsPlaced++;
            return Task.FromResult(new BrokerOrder($"stop-{StopsPlaced}", closeIntent, OrderStatus.Accepted, 0, 0m, T0, null));
        }

        public AlternativeProtectiveOrderType AlternativeProtectiveOrderType => AlternativeProtectiveOrderType.StopLimit;

        public Task<AlternativeProtectiveOrderPlacement> PlaceAlternativeStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, decimal entryReferencePrice, Guid decisionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("本試験では S3 の保護レグを送らない");

        public Task<BrokerOrder> PlaceMarketOrderAsync(OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            Sent++;
            return Task.FromResult(new BrokerOrder($"close-{Sent}", closeIntent, OrderStatus.Accepted, 0, 0m, T0, null));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed record Harness(
        AppSvc Service,
        Broker Broker,
        MutableClock Clock,
        InMemoryOrderReservationStore Reservations,
        InMemoryExecutedOrderStore Executed,
        InMemoryProtectiveStopOrderStore Stops)
    {
        public OrderReservationReconciler Reconciler(bool gateOpen, ReservationProbeResult? probe = null) =>
            new(Reservations, Executed, new StubProbe(probe ?? ReservationProbeResult.NotPlaced), Broker, Clock,
                Options.Create(new ReconciliationOptions
                {
                    Enabled = true,
                    UseBrokerProbe = true,
                    ReleaseOnNotPlaced = new() { Simulate = gateOpen },
                }),
                Service);

        // 滞留の閾値を越えた後の巡回（予約から 3 時間後・閾値 2 時間）。
        public async Task<ReservationReconciliationResult> ReconcileAsync(bool gateOpen, ReservationProbeResult? probe = null)
        {
            Clock.UtcNow = T0.AddHours(3);
            return await Reconciler(gateOpen, probe).ReconcileAsync(Clock.UtcNow.AddHours(-2), 50);
        }
    }

    private static Harness NewHarness()
    {
        var broker = new Broker();
        var clock = new MutableClock();
        var reservations = new InMemoryOrderReservationStore();
        var executed = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var service = new AppSvc(broker, executed, reservations, clock, stops, brokerPositions: broker);
        return new Harness(service, broker, clock, reservations, executed, stops);
    }

    private static OrderApproved Entry(
        StopLossExecutionMethod method, OrderApprovalOrigin origin = OrderApprovalOrigin.TradeDecision) =>
        new(Guid.NewGuid(),
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
                719, 345m, PositionEffect.Open, StopLossPrice: 327.83m, FxRateToBase: 1m),
            719, T0, StopLossMethod: method, FromTradeDecision: true, Origin: origin);

    // 送る前に結果が不明になった新規建てを作る（予約は Reserved・保護の記録は残る・発注の記録は無い）。
    private static async Task<OrderApproved> DispatchIndeterminateAsync(
        Harness h, StopLossExecutionMethod method, OrderApprovalOrigin origin = OrderApprovalOrigin.TradeDecision)
    {
        var approved = Entry(method, origin);
        h.Broker.IndeterminateEntry = true;
        var act = () => h.Service.ExecuteAsync(approved);
        await act.Should().ThrowAsync<BrokerDispatchIndeterminateException>();
        h.Broker.IndeterminateEntry = false;

        h.Reservations.Find(approved.DecisionId)!.State.Should().Be(OrderDispatchState.Reserved, "前提: 送信結果が不明で据え置いた");
        h.Executed.FindByDecisionId(approved.DecisionId).Should().BeNull("前提: 発注の記録は無い");
        h.Stops.Find(approved.DecisionId).Should().NotBeNull("前提: 送る前に保護の記録を残した");
        return approved;
    }

    // T-10-2703（受け入れ基準 1）: 門による解放で S1 の行が閉じ、予約は Forgone になる。
    [Fact]
    public async Task T_10_2703_門による解放でS1の行が閉じ予約はForgoneになる()
    {
        var h = NewHarness();
        var entry = await DispatchIndeterminateAsync(h, StopLossExecutionMethod.SoftwareStop);
        h.Stops.Find(entry.DecisionId)!.State.Should().Be(ProtectiveStopState.Active, "前提: S1 は送る前に武装される");

        var result = await h.ReconcileAsync(gateOpen: true);

        result.Released.Should().Be(1);
        h.Reservations.Find(entry.DecisionId)!.State.Should().Be(OrderDispatchState.Forgone, "削除ではなく見送りとして閉じる");
        h.Stops.Find(entry.DecisionId)!.State.Should().Be(ProtectiveStopState.Completed, "発注の記録の無い S1 を閉じる");
        result.UnplacedClosures.Should().ContainSingle()
            .Which.Should().Be(new UnplacedEntryProtectionClosureResult(
                entry.DecisionId, UnplacedEntryProtectionClosure.SoftwareStopClosed, null));
        h.Broker.Sent.Should().Be(0, "リコンサイラ自身は発注しない");
    }

    // T-10-2704（受け入れ基準 1）: S0 / S3 の AwaitingEntry も閉じる（保護レグは張らない）。
    [Theory]
    [InlineData(StopLossExecutionMethod.BrokerStopOrder)]
    [InlineData(StopLossExecutionMethod.AlternativeBrokerOrderType)]
    public async Task T_10_2704_門による解放でAwaitingEntryが閉じる(StopLossExecutionMethod method)
    {
        var h = NewHarness();
        var entry = await DispatchIndeterminateAsync(h, method);
        h.Stops.Find(entry.DecisionId)!.State.Should().Be(ProtectiveStopState.AwaitingEntry, "前提");

        var result = await h.ReconcileAsync(gateOpen: true);

        h.Reservations.Find(entry.DecisionId)!.State.Should().Be(OrderDispatchState.Forgone);
        h.Stops.Find(entry.DecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        result.UnplacedClosures.Should().ContainSingle().Which.Closure.Should().Be(UnplacedEntryProtectionClosure.AwaitingEntryClosed);
        h.Broker.StopsPlaced.Should().Be(0, "建玉は無いので逆指値を張らない");
    }

    // T-10-2705（受け入れ基準 2・対照つき）: 解放の後は同じ銘柄・同じ方向の別の手法の新規建てが塞がれない。門が閉じていれば塞がれたまま。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T_10_2705_解放の後は同じ銘柄の別手法の新規建てが塞がれない(bool gateOpen)
    {
        var h = NewHarness();
        await DispatchIndeterminateAsync(h, StopLossExecutionMethod.SoftwareStop);
        await h.ReconcileAsync(gateOpen);

        var next = await h.Service.ExecuteAsync(Entry(StopLossExecutionMethod.BrokerStopOrder));

        if (gateOpen)
        {
            next.Forgone.Should().BeNull("閉じた S1 の行は併存の判定に載らない");
            next.Executed.Should().NotBeNull();
            h.Broker.Sent.Should().Be(1, "S0 の新規建てを送った");
        }
        else
        {
            next.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.StopLossMethodConflict, "対照: 門が閉じていれば S1 の行が残り塞ぐ");
            h.Broker.Sent.Should().Be(0);
        }
    }

    // T-10-2706（受け入れ基準 3）: 解放した承認を再投入しても発注しない（古い承認で送らない）。
    [Theory]
    [InlineData(StopLossExecutionMethod.SoftwareStop)]
    [InlineData(StopLossExecutionMethod.BrokerStopOrder)]
    public async Task T_10_2706_解放した承認を再投入しても発注しない(StopLossExecutionMethod method)
    {
        var h = NewHarness();
        var entry = await DispatchIndeterminateAsync(h, method);
        await h.ReconcileAsync(gateOpen: true);

        // _error からの再投入（3 時間後。ブローカーは今度は送れる状態）。
        var replay = await h.Service.ExecuteAsync(entry);

        replay.ForgoneReplaySuppressed.Should().BeTrue("見送り済みの承認は再配送されても発注しない");
        replay.Executed.Should().BeNull();
        h.Broker.Sent.Should().Be(0, "古い承認の指値を送らない");
        h.Stops.Find(entry.DecisionId)!.State.Should().Be(ProtectiveStopState.Completed, "再投入で S1 を武装し直さない");
    }

    // T-10-2707: 出どころの列を足す前の予約（ApprovalOrigin が null）でも、保護の記録を残した新規建てなら見送りとして閉じる（2026-10-05 の AAPL の形）。
    [Fact]
    public async Task T_10_2707_出どころの無い旧い予約でも保護の記録があれば見送りとして閉じる()
    {
        var h = NewHarness();
        var entry = await DispatchIndeterminateAsync(h, StopLossExecutionMethod.SoftwareStop, OrderApprovalOrigin.Unknown);
        h.Reservations.Find(entry.DecisionId)!.ApprovalOrigin.Should().BeNull("前提: 出どころを持たない予約");

        await h.ReconcileAsync(gateOpen: true);

        h.Reservations.Find(entry.DecisionId)!.State.Should().Be(OrderDispatchState.Forgone);
        h.Stops.Find(entry.DecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        (await h.Service.ExecuteAsync(entry)).ForgoneReplaySuppressed.Should().BeTrue();
    }

    // T-10-2708: 承認の経路の決済（利用者の手仕舞い。保護の記録は無い）も見送りとして閉じ、再投入で送らない。
    [Fact]
    public async Task T_10_2708_承認の経路の決済も見送りとして閉じ再投入で送らない()
    {
        var h = NewHarness();
        var close = new OrderApproved(Guid.NewGuid(),
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
                10, 345m, PositionEffect.Close, FxRateToBase: 1m),
            10, T0, Origin: OrderApprovalOrigin.OwnerClose);
        h.Reservations.TryReserve(close.DecisionId, T0, BrokerProvider.MoomooSimulate,
            approvalOrigin: OrderApprovalOrigin.OwnerClose, positionEffect: PositionEffect.Close).Should().BeTrue();

        var result = await h.ReconcileAsync(gateOpen: true);

        result.Released.Should().Be(1);
        h.Reservations.Find(close.DecisionId)!.State.Should().Be(OrderDispatchState.Forgone);
        result.UnplacedClosures.Should().ContainSingle().Which.Closure.Should().Be(UnplacedEntryProtectionClosure.NoProtectionRecord);
    }

    // T-10-2709（否定形）: 保護の機構のレグ（出どころ null・保護の記録の EntryDecisionId ではない）は従来どおり削除する
    // ——撃ち直すのは機構自身であり、見送りにすると建玉が無防備のまま残る。
    [Fact]
    public async Task T_10_2709_保護の機構のレグは従来どおり削除する_否定形()
    {
        var h = NewHarness();
        var leg = Guid.NewGuid();
        h.Reservations.TryReserve(leg, T0, BrokerProvider.MoomooSimulate, positionEffect: PositionEffect.Close).Should().BeTrue();

        var result = await h.ReconcileAsync(gateOpen: true);

        result.Released.Should().Be(1);
        h.Reservations.Find(leg).Should().BeNull("機構のレグは削除（同じ DecisionId で撃ち直せる）");
        result.UnplacedClosures.Should().BeEmpty();
    }

    // T-10-2710（否定形）: 到達して決済の経路に入った S1 の行は閉じない（予約は見送りにするが、行は人が確かめる）。
    [Fact]
    public async Task T_10_2710_到達したS1の行は閉じない_否定形()
    {
        var h = NewHarness();
        var entry = await DispatchIndeterminateAsync(h, StopLossExecutionMethod.SoftwareStop);
        var row = h.Stops.Find(entry.DecisionId)!;
        h.Stops.TrySave(row with { TriggeredAt = T0.AddHours(1), TriggeredPrice = 327m }).Should().BeTrue();

        var result = await h.ReconcileAsync(gateOpen: true);

        h.Reservations.Find(entry.DecisionId)!.State.Should().Be(OrderDispatchState.Forgone);
        h.Stops.Find(entry.DecisionId)!.State.Should().Be(ProtectiveStopState.Active, "到達済みの行を古い写しで閉じない");
        result.UnplacedClosures.Should().ContainSingle().Which.Closure.Should().Be(UnplacedEntryProtectionClosure.LeftOpen);
    }

    // T-10-2711（否定形）: 門が閉じているあいだは予約も保護の記録も触らない（是正の射程は門が開いたときだけ）。
    [Fact]
    public async Task T_10_2711_門が閉じていれば予約も保護の記録も触らない_否定形()
    {
        var h = NewHarness();
        var entry = await DispatchIndeterminateAsync(h, StopLossExecutionMethod.SoftwareStop);

        var result = await h.ReconcileAsync(gateOpen: false);

        result.HeldNotPlaced.Should().Equal([entry.DecisionId]);
        h.Reservations.Find(entry.DecisionId)!.State.Should().Be(OrderDispatchState.Reserved);
        h.Stops.Find(entry.DecisionId)!.State.Should().Be(ProtectiveStopState.Active);
        result.UnplacedClosures.Should().BeEmpty();
    }

    // T-10-2712（否定形）: 照会が不明（Indeterminate）なら、門が開いていても何も閉じない。
    [Fact]
    public async Task T_10_2712_照会が不明なら門が開いていても何も閉じない_否定形()
    {
        var h = NewHarness();
        var entry = await DispatchIndeterminateAsync(h, StopLossExecutionMethod.SoftwareStop);

        var result = await h.ReconcileAsync(gateOpen: true, ReservationProbeResult.Indeterminate);

        result.Indeterminate.Should().Be(1);
        h.Reservations.Find(entry.DecisionId)!.State.Should().Be(OrderDispatchState.Reserved);
        h.Stops.Find(entry.DecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }
    private sealed class ThrowingClose : IReconciledEntryProtection
    {
        public Task<ReconciledEntryProtectionOutcome> ProtectAsync(ExecutionRecord confirmed, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public bool HasEntryProtectionRecord(Guid decisionId) => true;

        public UnplacedEntryProtectionClosure CloseUnplacedEntryProtection(Guid decisionId) =>
            throw new InvalidOperationException("保護記録ストアが落ちた（テスト）");
    }

    // T-10-2713: 保護の記録を閉じる処理が落ちても、予約の見送りは巻き戻さず、閉じられなかったことを記録として返す（巡回は止めない）。
    [Fact]
    public async Task T_10_2713_記録を閉じる処理が落ちても見送りは巻き戻さず失敗を返す()
    {
        var h = NewHarness();
        var entry = Guid.NewGuid();
        h.Reservations.TryReserve(entry, T0, BrokerProvider.MoomooSimulate).Should().BeTrue();
        h.Clock.UtcNow = T0.AddHours(3);
        var reconciler = new OrderReservationReconciler(
            h.Reservations, h.Executed, new StubProbe(ReservationProbeResult.NotPlaced), h.Broker, h.Clock,
            Options.Create(new ReconciliationOptions { Enabled = true, ReleaseOnNotPlaced = new() { Simulate = true } }),
            new ThrowingClose());

        var result = await reconciler.ReconcileAsync(h.Clock.UtcNow.AddHours(-2), 50);

        result.Failed.Should().Be(0, "予約の見送りは済んでいる");
        h.Reservations.Find(entry)!.State.Should().Be(OrderDispatchState.Forgone);
        var closure = result.UnplacedClosures.Should().ContainSingle().Subject;
        closure.Closure.Should().Be(UnplacedEntryProtectionClosure.LeftOpen);
        closure.Failure.Should().BeOfType<InvalidOperationException>();
    }
}
