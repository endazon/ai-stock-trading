using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution.PollOrderFills;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;
using Behavior = OrderExecutionService.Tests.StopLegScriptedBroker.StopBehavior;

namespace OrderExecutionService.Tests;

// 🔴 FR-10, UC-02, ADR-0016 決定2(b), ADR-0040 決定1（S0 / S3）, #1422, IADR-0569: 保護逆指値が未受理のときのエントリー取消は、
// **送った後に照会して終端を確かめてから**「取消済み」と扱う。取消の窓で約定した分は成行で手仕舞い（業務フロー 02 の表）、
// 確かめられなければ「取消の確認待ち」で残して約定追跡の終端化で後始末する。
// 殺す変異: 取消の後の照会を外す（取消成功で即 EntryCancelled）→ AC2・AC3・通しの試験が赤。
//           約定追跡のフックを外す → 通しの試験が赤。後始末の「確認待ちか」の確認を外す → 2 回目の成行・Active 行の後始末で赤。
public class UnconfirmedEntryCancelTests
{
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => StopLegScriptedBroker.Now;
    }

    private sealed record Harness(
        AppSvc Service,
        StopLegScriptedBroker Broker,
        InMemoryExecutedOrderStore Store,
        InMemoryProtectiveStopOrderStore Stops,
        OrderFillPoller Poller);

    private static Harness NewHarness(StopLegScriptedBroker broker)
    {
        // #1048, IADR-0481 決定2: S0・S3 の新規建ては記録の無い建玉がある銘柄では見送る。送る前の口座（建玉なし）を模す。
        broker.Positions = [];
        var store = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var clock = new FakeClock();
        var service = new AppSvc(broker, store, reservations, clock, stops, brokerPositions: broker);
        var poller = new OrderFillPoller(broker, store, clock, protectiveStops: stops, entryCancelResolution: service);
        return new Harness(service, broker, store, stops, poller);
    }

    // 未約定のエントリー（Accepted・約定 0）と、拒否される逆指値（S0）。
    private static StopLegScriptedBroker RejectingBroker() =>
        new() { Stop = Behavior.Reject, EntryStatus = OrderStatus.Accepted, EntryFilled = 0 };

    private static OrderApproved Approved(StopLossExecutionMethod method = StopLossExecutionMethod.BrokerStopOrder)
    {
        var intent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 1_000m,
            PositionEffect.Open, StopLossPrice: 950m, FxRateToBase: 1m);
        return new OrderApproved(Guid.NewGuid(), intent, intent.Quantity, StopLegScriptedBroker.Now, StopLossMethod: method);
    }

    private static BrokerOrder EntryAt(OrderStatus status, int filled) =>
        new("entry-1", StopLegScriptedBroker.CloseIntent(10), status, filled, filled > 0 ? 1_001m : 0m,
            StopLegScriptedBroker.Now, OrderStatusLifecycle.IsTerminal(status) ? StopLegScriptedBroker.Now : null);

    // ---- AC1: 取消を照会で確かめた（終端・約定 0）ときだけ取消済み ----

    // T-10-2974
    [Theory]
    [InlineData(StopLossExecutionMethod.BrokerStopOrder)]
    [InlineData(StopLossExecutionMethod.AlternativeBrokerOrderType)]
    public async Task AC1_取消の後の照会で約定0の終端を確かめたら取消済みとして文脈を閉じる(StopLossExecutionMethod method)
    {
        var h = NewHarness(RejectingBroker());
        var approved = Approved(method);

        var result = await h.Service.ExecuteAsync(approved);

        h.Broker.CancelCount.Should().Be(1);
        h.Broker.MarketCloseCount.Should().Be(0);
        result.CoverageLost!.Remediation.Should().Be(ProtectiveStopRemediation.EntryCancelled);
        h.Stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // ---- AC2: 取消の窓で約定していたら約定数量を成行で手仕舞う ----

    // T-10-2975
    [Theory]
    [InlineData(OrderStatus.Filled, 10, 10)]   // 取消が間に合わず全量約定
    [InlineData(OrderStatus.Cancelled, 3, 3)]  // 一部約定の後に取り消せた
    public async Task AC2_取消の後の照会で約定があれば約定数量を成行で手仕舞い_取消済みと主張しない(
        OrderStatus observed, int filled, int expectedClose)
    {
        var broker = RejectingBroker();
        broker.Orders["entry-1"] = EntryAt(observed, filled);
        var h = NewHarness(broker);
        var approved = Approved();

        var result = await h.Service.ExecuteAsync(approved);

        h.Broker.MarketCloseCount.Should().Be(1, "既に約定していれば即座に成行で手仕舞う（業務フロー 02 の表）");
        var lost = result.CoverageLost!;
        lost.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed);
        lost.Quantity.Should().Be(expectedClose);
        lost.CloseIntent!.Quantity.Should().Be(expectedClose);
        lost.CloseIntent.Side.Should().Be(TradeSide.Sell);
        h.Stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // ---- AC3: 確かめられなければ取消済みと主張せず、確認待ちで残す ----

    // T-10-2976
    [Theory]
    [InlineData(true)]   // 照会が非終端（Cancelling_* は非終端へ倒れる）
    [InlineData(false)]  // 照会できない（不明）
    public async Task AC3_取消の後の照会が非終端か不明なら_取消済みと主張せず確認待ちで残す_否定形(bool nonTerminal)
    {
        var broker = RejectingBroker();
        broker.Orders["entry-1"] = nonTerminal ? EntryAt(OrderStatus.Accepted, 0) : null;
        var h = NewHarness(broker);
        var approved = Approved();

        var result = await h.Service.ExecuteAsync(approved);

        var lost = result.CoverageLost!;
        lost.Remediation.Should().Be(ProtectiveStopRemediation.EntryCancelUnconfirmed,
            "取消要求の後にも約定し得る。照会で終端を確かめるまでは「建玉は生じていない」と言わない");
        lost.Cause.Should().Be(ProtectiveStopLossCause.RejectedAtEntry);
        lost.CloseDecisionId.Should().BeNull("何も送っていない");
        lost.CloseIntent.Should().BeNull();
        h.Broker.MarketCloseCount.Should().Be(0, "非終端のまま約定数で手仕舞うと、残りの約定がまた無保護で残る");
        h.Stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.AwaitingEntryCancel);
        h.Stops.FindActive(100).Should().BeEmpty("常駐ガードの巡回対象ではない");
        h.Stops.FindOpenFor("AAPL", Market.UnitedStates, TradeSide.Buy).Should().ContainSingle("生きているかもしれないエントリーとして併存の関門に数える");
    }

    // ---- AC4: 約定追跡が終端化を観測したら 1 回だけ後始末する（通し） ----

    // T-10-2977
    [Fact]
    public async Task AC4_確認待ちのエントリーが取消の窓で約定したら_約定追跡の終端化で成行手仕舞いを1回だけ出す()
    {
        var broker = RejectingBroker();
        broker.Orders["entry-1"] = EntryAt(OrderStatus.Accepted, 0);
        var h = NewHarness(broker);
        var approved = Approved();
        (await h.Service.ExecuteAsync(approved)).CoverageLost!.Remediation
            .Should().Be(ProtectiveStopRemediation.EntryCancelUnconfirmed);

        // 取消は間に合わず、エントリーは全量約定して終端した。
        broker.Orders["entry-1"] = EntryAt(OrderStatus.Filled, 10);
        var first = await h.Poller.PollOnceAsync(TimeSpan.FromHours(24), batchSize: 100);

        first.Executed.Should().ContainSingle(e => e.DecisionId == approved.DecisionId && e.Status == OrderStatus.Filled);
        var resolved = first.EntryCancelResolutions.Should().ContainSingle().Subject;
        resolved.EntryDecisionId.Should().Be(approved.DecisionId);
        resolved.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed);
        resolved.Quantity.Should().Be(10);
        resolved.CloseIntent!.Side.Should().Be(TradeSide.Sell);
        h.Broker.MarketCloseCount.Should().Be(1);
        h.Stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.Completed);

        // 次の巡回では何も起きない（エントリーは終端・成行の記録は約定済み）。
        var second = await h.Poller.PollOnceAsync(TimeSpan.FromHours(24), batchSize: 100);
        (second.EntryCancelResolutions ?? []).Should().BeEmpty();
        h.Broker.MarketCloseCount.Should().Be(1);
    }

    // T-10-2978
    [Fact]
    public async Task AC4_確認待ちのエントリーが約定0で終端したら取消を確定する()
    {
        var broker = RejectingBroker();
        broker.Orders["entry-1"] = null;
        var h = NewHarness(broker);
        var approved = Approved();
        await h.Service.ExecuteAsync(approved);

        broker.Orders["entry-1"] = EntryAt(OrderStatus.Cancelled, 0);
        var result = await h.Poller.PollOnceAsync(TimeSpan.FromHours(24), batchSize: 100);

        result.EntryCancelResolutions.Should().ContainSingle()
            .Which.Remediation.Should().Be(ProtectiveStopRemediation.EntryCancelled);
        h.Broker.MarketCloseCount.Should().Be(0);
        h.Stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // T-10-2979
    [Fact]
    public async Task AC4_同じ終端化を2回渡しても成行は1本だけ()
    {
        var broker = RejectingBroker();
        broker.Orders["entry-1"] = null;
        var h = NewHarness(broker);
        var approved = Approved();
        await h.Service.ExecuteAsync(approved);
        var terminal = h.Store.FindByDecisionId(approved.DecisionId)! with { Status = OrderStatus.Filled, FilledQuantity = 10 };

        var first = await h.Service.ResolveUnconfirmedEntryCancelAsync(terminal, CancellationToken.None);
        var second = await h.Service.ResolveUnconfirmedEntryCancelAsync(terminal, CancellationToken.None);

        first!.Remediation.Should().Be(ProtectiveStopRemediation.PositionClosed);
        second.Should().BeNull();
        h.Broker.MarketCloseCount.Should().Be(1);
    }

    // T-10-2979
    [Fact]
    public async Task AC4_約定数は承認数量を上限にする()
    {
        var broker = RejectingBroker();
        broker.Orders["entry-1"] = null;
        var h = NewHarness(broker);
        var approved = Approved();
        await h.Service.ExecuteAsync(approved);
        var terminal = h.Store.FindByDecisionId(approved.DecisionId)! with { Status = OrderStatus.Filled, FilledQuantity = 12 };

        var resolved = await h.Service.ResolveUnconfirmedEntryCancelAsync(terminal, CancellationToken.None);

        resolved!.Quantity.Should().Be(10, "承認数量を超えて決済すると反対方向の建玉を生む");
    }

    // ---- AC5: 確認待ちでないエントリー・非終端では何もしない ----

    // T-10-2979
    [Fact]
    public async Task AC5_非終端の記録では後始末しない_否定形()
    {
        var broker = RejectingBroker();
        broker.Orders["entry-1"] = null;
        var h = NewHarness(broker);
        var approved = Approved();
        await h.Service.ExecuteAsync(approved);
        var pending = h.Store.FindByDecisionId(approved.DecisionId)! with { Status = OrderStatus.PartiallyFilled, FilledQuantity = 4 };

        var resolved = await h.Service.ResolveUnconfirmedEntryCancelAsync(pending, CancellationToken.None);

        resolved.Should().BeNull();
        h.Broker.MarketCloseCount.Should().Be(0);
        h.Stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.AwaitingEntryCancel);
    }

    // T-10-2983
    [Fact]
    public async Task AC4_後始末の成行手仕舞いが例外で落ちても_無音にせず解消にも失敗を返す()
    {
        var broker = RejectingBroker();
        broker.Orders["entry-1"] = null;
        broker.Positions = [];
        var store = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var approved = Approved();
        // 成行手仕舞いのレグの予約だけが落ちる（DB 障害）。
        var reservations = new ThrowingReserveStore(
            new InMemoryOrderReservationStore(), ProtectiveStopIds.CloseDecisionId(approved.DecisionId, attempt: 1));
        var service = new AppSvc(broker, store, reservations, new FakeClock(), stops, brokerPositions: broker);
        await service.ExecuteAsync(approved);
        broker.Orders["entry-1"] = EntryAt(OrderStatus.Filled, 10);
        var poller = new OrderFillPoller(broker, store, new FakeClock(), protectiveStops: stops, entryCancelResolution: service);

        var first = await poller.PollOnceAsync(TimeSpan.FromHours(24), batchSize: 100);

        // (1) 保護喪失が発行される（Critical の None＝解消にも失敗。通知・監査の既存の経路に乗る）。
        var resolved = first.EntryCancelResolutions.Should().ContainSingle().Subject;
        resolved.Remediation.Should().Be(ProtectiveStopRemediation.None, "例外を投げ返すと保護喪失が 1 件も出ず無音になる");
        resolved.Quantity.Should().Be(10);
        // (2) 巡回は止まらない（エントリーの約定は発行される）。
        first.Failed.Should().Be(0);
        first.Executed.Should().ContainSingle(e => e.DecisionId == approved.DecisionId && e.Status == OrderStatus.Filled);
        // (3) 二重に手仕舞いしない（行は Completed・同じ終端化をもう一度渡しても何もしない・次の巡回でも何もしない）。
        broker.MarketCloseCount.Should().Be(0);
        stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        var terminal = store.FindByDecisionId(approved.DecisionId)!;
        (await service.ResolveUnconfirmedEntryCancelAsync(terminal, CancellationToken.None)).Should().BeNull();
        var second = await poller.PollOnceAsync(TimeSpan.FromHours(24), batchSize: 100);
        (second.EntryCancelResolutions ?? []).Should().BeEmpty();
        broker.MarketCloseCount.Should().Be(0);
    }

    // T-10-2980
    [Fact]
    public async Task AC5_逆指値が受理されたエントリーの終端化では何もしない_否定形()
    {
        var broker = new StopLegScriptedBroker { Stop = Behavior.Accept, EntryStatus = OrderStatus.Accepted, EntryFilled = 0 };
        broker.Orders["entry-1"] = EntryAt(OrderStatus.Filled, 10);
        var h = NewHarness(broker);
        var approved = Approved();
        (await h.Service.ExecuteAsync(approved)).StopPlaced.Should().NotBeNull();

        var result = await h.Poller.PollOnceAsync(TimeSpan.FromHours(24), batchSize: 100);

        result.Executed.Should().Contain(e => e.DecisionId == approved.DecisionId);
        (result.EntryCancelResolutions ?? []).Should().BeEmpty();
        h.Broker.MarketCloseCount.Should().Be(0);
        h.Broker.CancelCount.Should().Be(0);
        h.Stops.Find(approved.DecisionId)!.State.Should().Be(ProtectiveStopState.Active);
    }

    // T-10-2980
    [Fact]
    public async Task 後始末の口が例外でも約定追跡は止まらない()
    {
        var broker = RejectingBroker();
        broker.Orders["entry-1"] = null;
        broker.Positions = [];
        var store = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var clock = new FakeClock();
        var service = new AppSvc(broker, store, new InMemoryOrderReservationStore(), clock, stops, brokerPositions: broker);
        var approved = Approved();
        await service.ExecuteAsync(approved);
        broker.Orders["entry-1"] = EntryAt(OrderStatus.Filled, 10);
        var poller = new OrderFillPoller(broker, store, clock, protectiveStops: stops, entryCancelResolution: new ThrowingResolution());

        var result = await poller.PollOnceAsync(TimeSpan.FromHours(24), batchSize: 100);

        result.Failed.Should().Be(0);
        result.Terminalized.Should().Be(1);
        result.Executed.Should().ContainSingle(e => e.DecisionId == approved.DecisionId);
    }

    private sealed class ThrowingResolution : OrderExecutionService.Features.OrderExecution.IUnconfirmedEntryCancelResolution
    {
        public Task<ProtectiveStopCoverageLost?> ResolveUnconfirmedEntryCancelAsync(
            ExecutionRecord terminalEntry, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("後始末に失敗（テスト）");
    }
}
