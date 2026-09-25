using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;
using Behavior = OrderExecutionService.Tests.StopLegScriptedBroker.StopBehavior;

namespace OrderExecutionService.Tests;

// 🔴 FR-10, FR-05, FR-12, ADR-0040 決定1, #853, IADR-0210（2026-09-25 追記）, IADR-0428 決定4:
// **突合（client order id）で「発注済み」と確定したエントリーに、承認時の手法で保護レグを張る**（オーナー裁定 2026-09-25 の 2）。
// 従来の OrderReservationReconciler は記録の保存と OrderExecuted の発行までで、保護レグを張らなかった
// ——送信結果が不明のまま後から発注済みと判明したエントリーは、保護レグを持たないまま台帳へ載っていた。
// 本クラスは発注執行そのもの（OrderExecutionAppService）を保護の口として突合へ渡し、突合 → 保護の結線を固定する。
public class ReconciledEntryProtectionTests
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
        public Task<ReservationProbeResult> ProbeAsync(
            OrderDispatchReservation reservation, CancellationToken cancellationToken = default) =>
            Task.FromResult(fn(reservation));
    }

    private sealed class RecordingSink : IReservationReconciliationSink
    {
        public List<object> Order { get; } = [];

        public List<ReconciledEntryProtectionEmission> Protections { get; } = [];

        public Task EmitAsync(ReservationTerminalizationEmission emission)
        {
            Order.Add(emission);
            return Task.CompletedTask;
        }

        public Task EmitProtectionAsync(ReconciledEntryProtectionEmission emission)
        {
            Order.Add(emission);
            Protections.Add(emission);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingProtection(Guid throwFor, IReconciledEntryProtection inner) : IReconciledEntryProtection
    {
        public Task<ReconciledEntryProtectionOutcome> ProtectAsync(ExecutionRecord confirmed, CancellationToken cancellationToken) =>
            confirmed.DecisionId == throwFor
                ? throw new InvalidOperationException("保護の口が落ちた（テスト）")
                : inner.ProtectAsync(confirmed, cancellationToken);
    }

    private sealed record Harness(
        OrderReservationReconciler Reconciler,
        StopLegScriptedBroker Broker,
        InMemoryOrderReservationStore Reservations,
        InMemoryExecutedOrderStore Executed,
        InMemoryProtectiveStopOrderStore Stops,
        RecordingSink Sink);

    // プローブが返すのはブローカーの注文から再構成した近似（PositionEffect=Open 固定・ProductType=Cash・Mode=paper）である
    // （MoomooReservationBrokerProbe と同じ）。承認の文脈（手法・損切りライン）は持たない。
    private static BrokerOrder EntryAtBroker(string orderId, OrderStatus status, int filled) =>
        new(orderId,
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
                10, 1_000m, PositionEffect.Open),
            status, filled, filled > 0 ? 1_000m : 0m, PlacedAt: StalledAt,
            CompletedAt: OrderStatusLifecycle.IsTerminal(status) ? StalledAt : null);

    private static Harness NewHarness(
        StopLegScriptedBroker broker,
        Func<OrderDispatchReservation, ReservationProbeResult> probe,
        Func<IReconciledEntryProtection, IReconciledEntryProtection?>? wrap = null)
    {
        var reservations = new InMemoryOrderReservationStore();
        var executed = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var clock = new FakeClock();
        IReconciledEntryProtection service = new AppSvc(broker, executed, reservations, clock, stops, brokerPositions: broker);
        var reconciler = new OrderReservationReconciler(
            reservations, executed, new CallbackProbe(probe), broker, clock,
            Microsoft.Extensions.Options.Options.Create(new ReconciliationOptions { Enabled = true }),
            wrap is null ? service : wrap(service));
        return new Harness(reconciler, broker, reservations, executed, stops, new RecordingSink());
    }

    // エントリーを送る前に残した承認時の保護の文脈（OrderExecutionAppService.RecordAwaitingProtection と同じ形）。
    private static ProtectiveStopOrder Awaiting(Guid entry, StopLossExecutionMethod mechanism) =>
        new(entry, ProtectiveStopIds.StopDecisionId(entry, 1), StopOrderId: string.Empty, "AAPL", Market.UnitedStates,
            TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, Attempt: 0,
            ProtectiveStopState.AwaitingEntry, StalledAt, StalledAt, Mechanism: mechanism);

    private static Guid Seed(Harness h, ProtectiveStopOrder? row)
    {
        var entry = row?.EntryDecisionId ?? Guid.NewGuid();
        h.Reservations.TryReserve(entry, StalledAt);
        if (row is not null)
            h.Stops.Save(row);
        return entry;
    }

    // ---- 🔴 T-10-1070: AwaitingEntry（S0 / S3）に承認時の手法で張る ----

    [Fact]
    public async Task 突合で確定したS0のエントリーにブローカー側の逆指値を張る()
    {
        var entry = Guid.NewGuid();
        var h = NewHarness(new StopLegScriptedBroker { Stop = Behavior.Accept },
            _ => ReservationProbeResult.Placed(EntryAtBroker("entry-9", OrderStatus.Filled, 10)));
        Seed(h, Awaiting(entry, StopLossExecutionMethod.BrokerStopOrder));

        var result = await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        h.Broker.StopPlaceCount.Should().Be(1);
        h.Broker.StopDecisionIds.Should().Equal([ProtectiveStopIds.StopDecisionId(entry, 1)], "平常の経路と同じ決定的なレグ");
        h.Broker.LastStopIntent!.Side.Should().Be(TradeSide.Sell);
        h.Broker.LastStopIntent.Quantity.Should().Be(10);
        h.Broker.LastStopIntent.Mode.Should().Be(BrokerProvider.MoomooSimulate, "承認時の文脈（プローブの近似ではない）");
        var row = h.Stops.Find(entry)!;
        row.State.Should().Be(ProtectiveStopState.Active);
        row.StopOrderId.Should().NotBeEmpty();
        row.TriggerPrice.Should().Be(950m);

        var protection = result.Protections.Should().ContainSingle().Subject;
        protection.ProbeConfirmed.Should().BeTrue();
        protection.Outcome!.Kind.Should().Be(ReconciledEntryProtectionKind.BrokerStopPlaced);
        protection.Outcome.Events.Should().ContainSingle().Which.Should().BeOfType<ProtectiveStopPlaced>();
        h.Sink.Order.Should().HaveCount(2);
        h.Sink.Order[0].Should().BeOfType<ReservationTerminalizationEmission>("確定済みの OrderExecuted と所見が先に出る（IADR-0371）");
        h.Sink.Order[1].Should().BeOfType<ReconciledEntryProtectionEmission>();
    }

    [Fact]
    public async Task 突合で確定したS3のエントリーには代替注文種別で張る()
    {
        var entry = Guid.NewGuid();
        var h = NewHarness(new StopLegScriptedBroker { Stop = Behavior.Accept },
            _ => ReservationProbeResult.Placed(EntryAtBroker("entry-9", OrderStatus.Filled, 10)));
        Seed(h, Awaiting(entry, StopLossExecutionMethod.AlternativeBrokerOrderType));

        var result = await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        h.Broker.AlternativeStopCount.Should().Be(1);
        h.Broker.StopPlaceCount.Should().Be(0, "S3 を S0 へ読み替えない");
        var events = result.Protections.Single().Outcome!.Events;
        events.Select(e => e.GetType()).Should().Equal(
            [typeof(AlternativeProtectiveStopAttempted), typeof(ProtectiveStopPlaced)], "平常の経路と同じ順（試行の記録 → 受理）");
    }

    [Fact]
    public async Task 突合で確定したエントリーの逆指値が届いたか不明なら据え置く_否定形()
    {
        var entry = Guid.NewGuid();
        var h = NewHarness(new StopLegScriptedBroker { Stop = Behavior.Indeterminate },
            _ => ReservationProbeResult.Placed(EntryAtBroker("entry-9", OrderStatus.Filled, 10)));
        Seed(h, Awaiting(entry, StopLossExecutionMethod.BrokerStopOrder));

        var result = await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        var outcome = result.Protections.Single().Outcome!;
        outcome.Kind.Should().Be(ReconciledEntryProtectionKind.StopDispatchHeld);
        outcome.Events.OfType<ProtectiveStopCoverageLost>().Single().Remediation
            .Should().Be(ProtectiveStopRemediation.StopDispatchIndeterminate);
        h.Broker.CancelCount.Should().Be(0);
        h.Broker.MarketCloseCount.Should().Be(0);
        h.Stops.Find(entry)!.IsStopDispatchPending.Should().BeTrue("常駐ガードが巡回する");
        h.Reservations.Find(ProtectiveStopIds.StopDecisionId(entry, 1))!.State.Should().Be(OrderDispatchState.Reserved);
    }

    [Fact]
    public async Task 突合で確定した未約定のエントリーの逆指値が拒否されたらエントリーを取り消す()
    {
        // 平常の経路と同じ（逆指値を張れない建玉は持たない）。取り消すのはブローカーの注文 ID（突合が見つけた注文）。
        var entry = Guid.NewGuid();
        var h = NewHarness(new StopLegScriptedBroker { Stop = Behavior.Reject },
            _ => ReservationProbeResult.Placed(EntryAtBroker("entry-9", OrderStatus.Accepted, 0)));
        Seed(h, Awaiting(entry, StopLossExecutionMethod.BrokerStopOrder));

        var result = await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        var outcome = result.Protections.Single().Outcome!;
        outcome.Kind.Should().Be(ReconciledEntryProtectionKind.CoverageLost);
        outcome.Events.OfType<ProtectiveStopCoverageLost>().Single().Remediation
            .Should().Be(ProtectiveStopRemediation.EntryCancelled);
        h.Broker.CancelCount.Should().Be(1);
        h.Stops.Find(entry)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // ---- T-10-1071: S1 / 記録なし / 建玉なし / 終端の約定あり ----

    [Fact]
    public async Task 突合で確定したS1のエントリーは記録を変えず配置の事実だけを出す()
    {
        var entry = Guid.NewGuid();
        var h = NewHarness(new StopLegScriptedBroker(),
            _ => ReservationProbeResult.Placed(EntryAtBroker("entry-9", OrderStatus.Filled, 10)));
        var s1 = new ProtectiveStopOrder(
            entry, ProtectiveStopIds.SoftwareStopId(entry), string.Empty, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, 0, ProtectiveStopState.Active,
            StalledAt, StalledAt, Mechanism: StopLossExecutionMethod.SoftwareStop);
        Seed(h, s1);

        var result = await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        var outcome = result.Protections.Single().Outcome!;
        outcome.Kind.Should().Be(ReconciledEntryProtectionKind.SoftwareStopArmed);
        var armed = outcome.Events.Should().ContainSingle().Which.Should().BeOfType<SoftwareStopArmed>().Subject;
        armed.EntryDecisionId.Should().Be(entry);
        armed.StopLossPrice.Should().Be(950m);
        h.Broker.StopPlaceCount.Should().Be(0, "S1 はブローカーへ保護レグを出さない（今夜の稼働経路を変えない）");
        h.Stops.Find(entry).Should().BeEquivalentTo(s1 with { Version = h.Stops.Find(entry)!.Version }, "S1 の行は触らない");
    }

    [Fact]
    public async Task 保護記録が無ければ張らない_否定形()
    {
        // 予約・プローブの PositionEffect ではエントリーと判別できない（IADR-0362 決定 3）。手仕舞いレグに逆指値を重ねない。
        var h = NewHarness(new StopLegScriptedBroker(),
            _ => ReservationProbeResult.Placed(EntryAtBroker("close-9", OrderStatus.Filled, 10)));
        Seed(h, row: null);

        var result = await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        result.Protections.Single().Outcome!.Kind.Should().Be(ReconciledEntryProtectionKind.NoProtectionRecord);
        h.Broker.StopPlaceCount.Should().Be(0);
        h.Broker.AlternativeStopCount.Should().Be(0);
        h.Broker.CancelCount.Should().Be(0);
        h.Broker.MarketCloseCount.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 据え置いた逆指値やガードの成行手仕舞いの突合は保護レグとして扱い張らない(bool stopLeg)
    {
        // T-10-1071（続き・PR #1005 監査 5）: 突合が確定したのは Active な保護記録の保護レグ（据え置いた逆指値＝行の StopDecisionId／
        // ガードの成行手仕舞い＝この試行の CloseDecisionId）。「保護の記録が無い」とは言わず（Critical を出させない）、何も送らない。
        var entry = Guid.NewGuid();
        var h = NewHarness(new StopLegScriptedBroker { Stop = Behavior.Accept },
            _ => ReservationProbeResult.Placed(EntryAtBroker("leg-9", OrderStatus.Accepted, 0)));
        var pending = new ProtectiveStopOrder(
            entry, ProtectiveStopIds.StopDecisionId(entry, 2), string.Empty, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, 2, ProtectiveStopState.Active, StalledAt, StalledAt,
            RemainingProtected: 10);
        h.Stops.Save(stopLeg ? pending : pending with { StopOrderId = StopLegScriptedBroker.LapsedStopOrderId });
        var leg = stopLeg ? ProtectiveStopIds.StopDecisionId(entry, 2) : ProtectiveStopIds.CloseDecisionId(entry, 3);
        h.Reservations.TryReserve(leg, StalledAt);

        var result = await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        result.Protections.Single().Outcome!.Kind.Should().Be(ReconciledEntryProtectionKind.ProtectiveLeg);
        h.Broker.StopPlaceCount.Should().Be(0);
        h.Broker.CancelCount.Should().Be(0);
        h.Broker.MarketCloseCount.Should().Be(0);
    }

    [Fact]
    public async Task 約定0で終端したエントリーには張らず承認時の文脈を閉じる()
    {
        var entry = Guid.NewGuid();
        var h = NewHarness(new StopLegScriptedBroker(),
            _ => ReservationProbeResult.Placed(EntryAtBroker("entry-9", OrderStatus.Cancelled, 0)));
        Seed(h, Awaiting(entry, StopLossExecutionMethod.BrokerStopOrder));

        var result = await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        result.Protections.Single().Outcome!.Kind.Should().Be(ReconciledEntryProtectionKind.NotRequired);
        h.Broker.StopPlaceCount.Should().Be(0);
        h.Stops.Find(entry)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    [Fact]
    public async Task 一部約定で終端したエントリーには約定数量だけの逆指値を張る()
    {
        // 承認数量で張ると建玉を超える逆指値が発火して反対方向の建玉を生む。
        var entry = Guid.NewGuid();
        var h = NewHarness(new StopLegScriptedBroker { Stop = Behavior.Accept },
            _ => ReservationProbeResult.Placed(EntryAtBroker("entry-9", OrderStatus.Cancelled, 4)));
        Seed(h, Awaiting(entry, StopLossExecutionMethod.BrokerStopOrder));

        var result = await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        result.Protections.Single().Outcome!.Kind.Should().Be(ReconciledEntryProtectionKind.BrokerStopPlaced);
        h.Broker.LastStopIntent!.Quantity.Should().Be(4);
        h.Stops.Find(entry)!.Quantity.Should().Be(4);
    }

    // ---- 🔴 T-10-1072: 保護の失敗でも確定済みの 1 件の出口を失わない・競合では呼ばない・自己修復では呼ぶ ----

    [Fact]
    public async Task 保護の口が落ちても確定済みの発行は出ており残りの突合も続く_否定形()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var h = NewHarness(new StopLegScriptedBroker { Stop = Behavior.Accept },
            r => ReservationProbeResult.Placed(EntryAtBroker($"entry-{r.DecisionId:N}", OrderStatus.Filled, 10)),
            inner => new ThrowingProtection(first, inner));
        h.Reservations.TryReserve(first, StalledAt);
        h.Reservations.TryReserve(second, StalledAt.AddSeconds(1));
        h.Stops.Save(Awaiting(first, StopLossExecutionMethod.BrokerStopOrder));
        h.Stops.Save(Awaiting(second, StopLossExecutionMethod.BrokerStopOrder));

        var result = await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        result.Failed.Should().Be(0, "予約は確定済み。保護の失敗を「据え置き（次回巡回で再試行）」に数えない");
        result.Executed.Select(e => e.DecisionId).Should().Equal([first, second]);
        h.Sink.Order.OfType<ReservationTerminalizationEmission>().Should().HaveCount(2);
        var failed = h.Sink.Protections.Single(p => p.Confirmed.DecisionId == first);
        failed.Failure.Should().NotBeNull("落ちたことを出口へ渡す（無音にしない）");
        failed.Outcome.Should().BeNull();
        h.Sink.Protections.Single(p => p.Confirmed.DecisionId == second).Outcome!.Kind
            .Should().Be(ReconciledEntryProtectionKind.BrokerStopPlaced);
    }

    [Fact]
    public async Task 照会の間に通常フローが確定した競合では保護の口を呼ばない()
    {
        var entry = Guid.NewGuid();
        InMemoryExecutedOrderStore? executed = null;
        var h = NewHarness(new StopLegScriptedBroker { Stop = Behavior.Accept }, r =>
        {
            // 照会の待ちのあいだに通常フロー（OrderApprovedHandler）が同じ DecisionId を確定した（保護レグも通常フローが張る）。
            executed!.Save(new ExecutionRecord(
                r.DecisionId, "entry-normal", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
                PositionEffect.Open, 10, 1_000m, 10, 1_000m, OrderStatus.Filled, 0m, Now));
            return ReservationProbeResult.Placed(EntryAtBroker("entry-9", OrderStatus.Filled, 10));
        });
        executed = h.Executed;
        Seed(h, Awaiting(entry, StopLossExecutionMethod.BrokerStopOrder));

        var result = await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        result.Protections.Should().BeEmpty("通常フローが張っている最中に、別の経路から同じエントリーへ触らない");
        h.Broker.StopPlaceCount.Should().Be(0);
    }

    [Fact]
    public async Task 記録はあるのに確定していない自己修復でも保護が無ければ張る()
    {
        // 通常フローが記録を保存した直後（予約の確定の前）に止まった: 保護レグは張られていない（事前記録が AwaitingEntry のまま）。
        var entry = Guid.NewGuid();
        var h = NewHarness(new StopLegScriptedBroker { Stop = Behavior.Accept },
            _ => throw new InvalidOperationException("自己修復はブローカへ照会しない"));
        Seed(h, Awaiting(entry, StopLossExecutionMethod.BrokerStopOrder));
        h.Executed.Save(new ExecutionRecord(
            entry, "entry-self", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, 10, 1_000m, 10, 1_000m, OrderStatus.Filled, 0m, StalledAt));

        var result = await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        var protection = result.Protections.Should().ContainSingle().Subject;
        protection.ProbeConfirmed.Should().BeFalse();
        protection.Outcome!.Kind.Should().Be(ReconciledEntryProtectionKind.BrokerStopPlaced);
        h.Broker.StopPlaceCount.Should().Be(1);
    }

    [Fact]
    public async Task 保護の口が無い構成でも確定した1件ごとに張れないことを出口へ渡す()
    {
        var h = NewHarness(new StopLegScriptedBroker(),
            _ => ReservationProbeResult.Placed(EntryAtBroker("entry-9", OrderStatus.Filled, 10)),
            _ => null);
        Seed(h, Awaiting(Guid.NewGuid(), StopLossExecutionMethod.BrokerStopOrder));

        await h.Reconciler.ReconcileAsync(Cutoff, 50, h.Sink);

        var protection = h.Sink.Protections.Should().ContainSingle().Subject;
        protection.Outcome.Should().BeNull();
        protection.Failure.Should().BeNull("口が無い（黙って飛ばさず、常駐が Critical で残す）");
        h.Broker.StopPlaceCount.Should().Be(0);
    }
}
