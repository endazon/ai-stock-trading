using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;
using Behavior = OrderExecutionService.Tests.StopLegScriptedBroker.StopBehavior;

namespace OrderExecutionService.Tests;

// 🔴 FR-05, FR-10, NFR-09, #856, IADR-0441: **発注予約の自動リコンサイルは、状態が分からない予約に対してブローカーへ何も書かない**
// （原則 A: 不明は不在ではない）。
//
// リコンサイラ本体は発注も取消も呼ばない（IBrokerAdapter から読むのは Provider だけ）。ブローカーへの書き込み
// （逆指値・代替注文種別・成行手仕舞い・エントリーの取消）はすべて保護の口（OrderExecutionAppService.ProtectAsync。
// オーナー裁定 2026-09-25〔#853〕・IADR-0428 決定4）を通り、その口が呼ばれるのは「照会が発注済みと確定した」か
// 「記録がある（自己修復）」ときだけである。本クラスは**本物の保護の口**と送信回数を数えるブローカーでリコンサイラを組み、
// それ以外の分岐（不明・未発注〔門が閉〕・照会の例外・門を開けた解放）で**書き込みが 0 回**であることを固定する。
//
// 殺す変異: 保護の口を確定の前（照会の直後）へ動かす／Indeterminate・NotPlaced でも保護の口を呼ぶ／
// 解放の門の既定を開ける／解放の後にリコンサイラ自身がエントリーを送り直す。
public class ReconcilerAmbiguousStateNoBrokerWriteTests
{
    private static readonly DateTimeOffset Now = StopLegScriptedBroker.Now;
    private static readonly DateTimeOffset StalledAt = Now.AddHours(-3);
    private static readonly DateTimeOffset Cutoff = Now.AddHours(-2);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class CallbackProbe(Func<OrderDispatchReservation, ReservationProbeResult> fn) : IReservationBrokerProbe
    {
        public int Calls { get; private set; }

        public Task<ReservationProbeResult> ProbeAsync(
            OrderDispatchReservation reservation, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(fn(reservation));
        }
    }

    // 出口へ渡ったものを数える（確定しなかった予約は 1 件も渡らないこと＝IADR-0371 の前提も併せて見る）。
    private sealed class RecordingSink : IReservationReconciliationSink
    {
        public int Terminalizations { get; private set; }
        public int ProtectionEmissions { get; private set; }

        public Task EmitAsync(ReservationTerminalizationEmission emission)
        {
            Terminalizations++;
            return Task.CompletedTask;
        }

        public Task EmitProtectionAsync(ReconciledEntryProtectionEmission emission)
        {
            ProtectionEmissions++;
            return Task.CompletedTask;
        }
    }

    private sealed record Harness(
        OrderReservationReconciler Reconciler,
        StopLegScriptedBroker Broker,
        InMemoryOrderReservationStore Reservations,
        InMemoryExecutedOrderStore Executed,
        InMemoryProtectiveStopOrderStore Stops,
        CallbackProbe Probe,
        RecordingSink Sink);

    // options が null なら構成を渡さない（DI に構成が無い場合と同じ）。
    private static Harness NewHarness(
        Func<OrderDispatchReservation, ReservationProbeResult> probe,
        ReconciliationOptions? options,
        StopLegScriptedBroker? broker = null)
    {
        broker ??= new StopLegScriptedBroker { Stop = Behavior.Accept };
        var reservations = new InMemoryOrderReservationStore();
        var executed = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var clock = new FakeClock();
        var callbackProbe = new CallbackProbe(probe);
        IReconciledEntryProtection protection =
            new AppSvc(broker, executed, reservations, clock, stops, brokerPositions: broker);
        var reconciler = new OrderReservationReconciler(
            reservations, executed, callbackProbe, broker, clock,
            options is null ? null : Microsoft.Extensions.Options.Options.Create(options),
            protection);
        return new Harness(reconciler, broker, reservations, executed, stops, callbackProbe, new RecordingSink());
    }

    private static ReconciliationOptions Enabled(bool releaseOnNotPlaced = false) =>
        new() { Enabled = true, UseBrokerProbe = true, ReleaseOnNotPlaced = releaseOnNotPlaced };

    // エントリーを送る前に残した承認時の保護の文脈（S0）。**保護の口が呼ばれたら逆指値を送る**状態を用意しておく
    // ——この状態で書き込みが 0 回であることが、「口が呼ばれなかった」ことの証拠になる。
    private static ProtectiveStopOrder Awaiting(Guid entry) =>
        new(entry, ProtectiveStopIds.StopDecisionId(entry, 1), StopOrderId: string.Empty, "AAPL", Market.UnitedStates,
            TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, Attempt: 0,
            ProtectiveStopState.AwaitingEntry, StalledAt, StalledAt, Mechanism: StopLossExecutionMethod.BrokerStopOrder);

    private static Guid SeedAwaitingEntry(Harness h)
    {
        var entry = Guid.NewGuid();
        h.Reservations.TryReserve(entry, StalledAt);
        h.Stops.Save(Awaiting(entry));
        return entry;
    }

    private static BrokerOrder EntryAtBroker(string orderId) =>
        new(orderId,
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
                10, 1_000m, PositionEffect.Open),
            OrderStatus.Filled, 10, 1_000m, PlacedAt: StalledAt, CompletedAt: StalledAt);

    // ブローカーへの書き込みの総数（エントリー・逆指値・代替注文種別・成行・取消）。
    private static int BrokerWrites(StopLegScriptedBroker b) =>
        b.EntryCount + b.StopPlaceCount + b.AlternativeStopCount + b.MarketCloseCount + b.CancelCount;

    // 予約・記録・保護記録・出口が「何も起きていない」ままであること。
    private static void AssertUntouched(Harness h, Guid entry)
    {
        BrokerWrites(h.Broker).Should().Be(0, "状態が分からない予約に対して、ブローカーへ何も書かない（原則 A）");
        h.Reservations.Find(entry)!.State.Should().Be(OrderDispatchState.Reserved, "予約は解放も確定もしない（据え置き）");
        h.Executed.FindByDecisionId(entry).Should().BeNull("発注済みと確定していないので記録を作らない");
        h.Stops.Find(entry)!.State.Should().Be(ProtectiveStopState.AwaitingEntry, "保護の口は呼ばれていない");
        h.Reservations.Find(ProtectiveStopIds.StopDecisionId(entry, 1)).Should().BeNull("逆指値のレグの予約も取られていない");
        h.Sink.Terminalizations.Should().Be(0);
        h.Sink.ProtectionEmissions.Should().Be(0);
    }

    [Fact]
    public async Task 照会が不明ならブローカーへ何も書かず予約も保護記録も変えない_否定形()
    {
        // 🔴 T-10-1550
        var h = NewHarness(_ => ReservationProbeResult.Indeterminate, Enabled());
        var entry = SeedAwaitingEntry(h);

        var result = await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        h.Probe.Calls.Should().Be(1, "前提: 照会は行われた");
        result.Indeterminate.Should().Be(1);
        result.Protections.Should().BeEmpty();
        AssertUntouched(h, entry);
    }

    [Theory]
    [InlineData(false)] // 構成は渡すが門の値を書いていない（アプリ既定）
    [InlineData(true)]  // 配備と同じく明示的に false
    public async Task 照会が未発注でも門が閉じていればブローカーへ何も書かず据え置く_否定形(bool explicitFalse)
    {
        // 🔴 T-10-1551: 「未発注」の根拠は備考（remark）の突合であり、往復していなければ発注済みの注文も一致ゼロに見える。
        var options = explicitFalse ? Enabled(releaseOnNotPlaced: false) : new ReconciliationOptions { Enabled = true };
        var h = NewHarness(_ => ReservationProbeResult.NotPlaced, options);
        var entry = SeedAwaitingEntry(h);

        var result = await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        result.HeldNotPlaced.Should().Equal([entry], "据え置きは無音にしない");
        result.Released.Should().Be(0);
        AssertUntouched(h, entry);
    }

    [Fact]
    public async Task 照会が例外で落ちたらブローカーへ何も書かず据え置く_否定形()
    {
        // 🔴 T-10-1552
        var h = NewHarness(_ => throw new TimeoutException("照会の返信待ちが切れた（テスト）"), Enabled());
        var entry = SeedAwaitingEntry(h);

        var result = await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        result.Failed.Should().Be(1);
        AssertUntouched(h, entry);
    }

    [Theory]
    [InlineData(false)] // 照会が不明
    [InlineData(true)]  // 照会が未発注（門が閉）
    public async Task 送信結果待ちの逆指値のレグは照会が確定しなければ逆指値も成行も送り直さない_否定形(bool notPlaced)
    {
        // 🔴 T-10-1553: 保護逆指値を送ったが届いたか不明で据え置いた建玉（保護記録は Active・注文 ID が空、逆指値のレグの予約が Reserved）。
        // 突合がそのレグを確定できないあいだは、リコンサイラは逆指値を送り直さず、成行にも倒さない
        //（逆指値が生きていれば、2 本目の逆指値・成行で反対方向の建玉を生む）。
        var h = NewHarness(
            _ => notPlaced ? ReservationProbeResult.NotPlaced : ReservationProbeResult.Indeterminate, Enabled());
        var entry = Guid.NewGuid();
        var stopLeg = ProtectiveStopIds.StopDecisionId(entry, 1);
        h.Reservations.TryReserve(entry, StalledAt);
        h.Reservations.MarkCompleted(entry, "entry-1", StalledAt);
        h.Reservations.TryReserve(stopLeg, StalledAt);
        h.Stops.Save(Awaiting(entry) with { State = ProtectiveStopState.Active, Attempt = 1 });

        var result = await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        result.Scanned.Should().Be(1, "前提: 走査されたのは逆指値のレグの予約だけ（エントリーは確定済み）");
        BrokerWrites(h.Broker).Should().Be(0, "届いたか分からない逆指値を送り直さない・成行にも倒さない");
        h.Reservations.Find(stopLeg)!.State.Should().Be(OrderDispatchState.Reserved);
        h.Stops.Find(entry)!.IsStopDispatchPending.Should().BeTrue("送信結果待ちのまま（常駐ガードが巡回する）");
        h.Sink.Terminalizations.Should().Be(0);
    }

    [Fact]
    public async Task 門を開けた解放でもリコンサイラ自身はエントリーを送り直さない()
    {
        // 🔴 T-10-1554: 解放は「再発注の許可」であって発注そのものではない。送り直すのは OrderApproved の再配送
        //（_error からの人手の再投入）か保護逆指値ガードの次の巡回であり、リコンサイラはブローカーへ何も書かない。
        // （門を開ける判断は #856 に残る。本試験は開けたときの挙動の境界を固定するだけで、配備の門は閉じたまま。）
        var h = NewHarness(_ => ReservationProbeResult.NotPlaced, Enabled(releaseOnNotPlaced: true));
        var entry = SeedAwaitingEntry(h);

        var result = await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        result.Released.Should().Be(1);
        h.Reservations.Find(entry).Should().BeNull("前提: 予約は解放（削除）された");
        BrokerWrites(h.Broker).Should().Be(0, "リコンサイラ自身は発注しない");
        h.Stops.Find(entry)!.State.Should().Be(ProtectiveStopState.AwaitingEntry, "保護の口は呼ばれていない");
        h.Sink.Terminalizations.Should().Be(0);
    }

    [Fact]
    public async Task 混在した巡回で書き込みは発注済みと確定した1件の逆指値1本だけ()
    {
        // T-10-1555（境界）: 確定した 1 件の保護が、同じ巡回の不明・未発注の予約へ漏れない。
        Guid placed = Guid.Empty, unknown = Guid.Empty, notPlaced = Guid.Empty;
        var h = NewHarness(r =>
            r.DecisionId == placed ? ReservationProbeResult.Placed(EntryAtBroker("entry-found"))
            : r.DecisionId == notPlaced ? ReservationProbeResult.NotPlaced
            : ReservationProbeResult.Indeterminate,
            Enabled());
        placed = SeedAwaitingEntry(h);
        unknown = SeedAwaitingEntry(h);
        notPlaced = SeedAwaitingEntry(h);

        var result = await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        result.Terminalized.Should().Be(1);
        result.Indeterminate.Should().Be(1);
        result.HeldNotPlaced.Should().Equal([notPlaced]);
        h.Broker.StopPlaceCount.Should().Be(1, "承認時の手法（S0）で確定した 1 件にだけ逆指値を張る（IADR-0428 決定4）");
        h.Broker.StopDecisionIds.Should().Equal([ProtectiveStopIds.StopDecisionId(placed, 1)]);
        (h.Broker.EntryCount + h.Broker.AlternativeStopCount + h.Broker.MarketCloseCount + h.Broker.CancelCount)
            .Should().Be(0, "他の書き込みは無い");
        h.Stops.Find(unknown)!.State.Should().Be(ProtectiveStopState.AwaitingEntry);
        h.Stops.Find(notPlaced)!.State.Should().Be(ProtectiveStopState.AwaitingEntry);
        h.Reservations.Find(unknown)!.State.Should().Be(OrderDispatchState.Reserved);
        h.Reservations.Find(notPlaced)!.State.Should().Be(OrderDispatchState.Reserved);
    }

    [Fact]
    public async Task 構成を渡さないリコンサイラは門が閉じた側に倒れ未発注でも書き込みも解放もしない_否定形()
    {
        // 🔴 T-10-1556: 構成が DI に無い＝解放してよい、にしない（IADR-0362 決定1）。
        var h = NewHarness(_ => ReservationProbeResult.NotPlaced, options: null);
        var entry = SeedAwaitingEntry(h);

        var result = await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        result.HeldNotPlaced.Should().Equal([entry]);
        AssertUntouched(h, entry);
    }
}
