using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
using OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 FR-10, FR-05, UC-02, ADR-0050 決定1, #1121, IADR-0466: **損切り（S1 の決済）は、判断の手仕舞いが処理中であることを理由に止まらない**
// （T-10-1813..T-10-1818・T-10-1820）。
//
// 実測（2026-09-29・SIMULATE）の裏返し: 証券会社は未約定の売りが押さえた株数を売れる数量から除く。判断の手仕舞い（全量・指値）が板に
// 残るあいだ、S1 の成行は「建玉が足りない」で拒否され、撃ち直しても同じ理由で拒否され続けた（是正前）。
// 是正: S1 の決済を送る**前**に、同じ建玉を売る判断の手仕舞いを取り消し、取消が確定してから送る（窓の前の端）。
// 保護の機構が出した決済（S0 / S3 の保護レグ・他の S1 の決済・失効した逆指値の成行手仕舞い）は取り消さない。
//
// 下の偽の証券会社は SIMULATE の実測どおり「生きている売り注文の残りを売れる数量から除く」。配置は PoC と同じ AAPL 1,428 株
// （S1 の記録 713 株 @331.67・715 株 @330.88）。
public class SoftwareStopDecisionCloseYieldTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 15, 13, 53, TimeSpan.Zero);

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class Order(int quantity)
    {
        public int Quantity { get; } = quantity;

        public OrderStatus Status { get; set; } = OrderStatus.Accepted;

        public int Filled { get; set; }
    }

    private sealed class FakeBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public int Position { get; set; }

        public Dictionary<string, Order> Orders { get; } = [];

        public List<(OrderIntent Intent, Guid DecisionId, OrderStatus Status)> MarketCloses { get; } = [];

        public List<string> Cancels { get; } = [];

        public int PositionQueries { get; private set; }

        /// <summary>取消が効くか（false なら取消の後も生きていると答える）。</summary>
        public bool CancelTakesEffect { get; set; } = true;

        /// <summary>取消の最中に約定する株数（取消の前に約定し、残りが取り消される）。</summary>
        public int FillDuringCancel { get; set; }

        public bool CancelThrows { get; set; }

        /// <summary>取消が効かない注文（取消の後も生きていると答える。注文ごと）。</summary>
        public HashSet<string> CancelIgnored { get; } = [];

        public HashSet<string> QueryReturnsNull { get; } = [];

        public HashSet<string> QueryThrows { get; } = [];

        public Func<string, Exception?> QueryThrowsFor { get; set; } = _ => null;

        public Func<string, Exception?> CancelThrowsFor { get; set; } = _ => null;

        /// <summary>成行を評価する直前に呼ばれる（窓の中で判断の手仕舞いが載る＝増える側のプローブ）。</summary>
        public Action? BeforeMarketClose { get; set; }

        private int Locked => Orders.Values
            .Where(o => !OrderStatusLifecycle.IsTerminal(o.Status))
            .Sum(o => o.Quantity - o.Filled);

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("発動は通常発注を行わない");

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            throw new NotSupportedException("S1 はブローカーへ逆指値を出さない");

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            BeforeMarketClose?.Invoke();
            BeforeMarketClose = null;
            // SIMULATE の実測: 生きている売り注文の残りは売れる数量から除かれる（Not enough positions）。
            var status = closeIntent.Quantity <= Position - Locked ? OrderStatus.Accepted : OrderStatus.Rejected;
            var id = $"s1-close-{MarketCloses.Count + 1}";
            MarketCloses.Add((closeIntent, decisionId, status));
            if (status == OrderStatus.Accepted)
                Orders[id] = new Order(closeIntent.Quantity);
            return Task.FromResult(new BrokerOrder(
                id, closeIntent, status, 0, 0m, T0, status == OrderStatus.Rejected ? T0 : null));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default)
        {
            if (QueryThrowsFor(orderId) is { } thrown)
                throw thrown;
            if (QueryThrows.Contains(orderId))
                throw new InvalidOperationException("照会の失敗（試験）");
            if (QueryReturnsNull.Contains(orderId) || !Orders.TryGetValue(orderId, out var order))
                return Task.FromResult<BrokerOrder?>(null);

            return Task.FromResult<BrokerOrder?>(new BrokerOrder(
                orderId, MinimalIntent, order.Status, order.Filled, order.Filled > 0 ? 331m : 0m, T0,
                OrderStatusLifecycle.IsTerminal(order.Status) ? T0 : null));
        }

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            Cancels.Add(orderId);
            if (CancelThrowsFor(orderId) is { } thrown)
                throw thrown;
            if (Orders.TryGetValue(orderId, out var order) && CancelTakesEffect && !CancelIgnored.Contains(orderId))
            {
                order.Filled += FillDuringCancel;
                Position -= FillDuringCancel;
                order.Status = order.Filled >= order.Quantity ? OrderStatus.Filled : OrderStatus.Cancelled;
            }

            if (CancelThrows)
                throw new InvalidOperationException("取消の失敗（試験）");
            return Task.CompletedTask;
        }

        /// <summary>#1222: この回数目以降の建玉照会を不明（null）で返す。</summary>
        public int? PositionsUnknownFrom { get; set; }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default)
        {
            PositionQueries++;
            if (PositionsUnknownFrom is { } from && PositionQueries >= from)
                return Task.FromResult<IReadOnlyList<BrokerPositionSnapshot>?>(null);
            return Task.FromResult<IReadOnlyList<BrokerPositionSnapshot>?>(Snapshot());
        }

        public IReadOnlyList<BrokerPositionSnapshot> Snapshot() =>
            Position == 0 ? [] : [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, Position, 331m)];

        private static readonly OrderIntent MinimalIntent = new(
            "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate, 1, 0m,
            PositionEffect.Close, StopLossPrice: null, 1m);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed record Fixture(
        SoftwareStopExecutor Executor, FakeBroker Broker, InMemoryProtectiveStopOrderStore Stops,
        InMemoryExecutedOrderStore Store, MutableClock Clock, RecordingLogger<SoftwareStopExecutor> Log,
        InMemoryOrderReservationStore Reservations);

    // 発注の記録の読み出し（FindPendingCloses）が落ちるストア（T-10-1825）。他は包んだ実装へ渡す。
    private sealed class PendingClosesThrowingStore(IExecutedOrderStore inner) : IExecutedOrderStore
    {
        public void Save(ExecutionRecord record) => inner.Save(record);
        public IReadOnlyList<ExecutionRecord> GetAll() => inner.GetAll();
        public ExecutionRecord? FindByDecisionId(Guid decisionId) => inner.FindByDecisionId(decisionId);
        public IReadOnlyList<ExecutionRecord> FindPendingSince(DateTimeOffset since, int batchSize) =>
            inner.FindPendingSince(since, batchSize);
        public IReadOnlyList<ExecutionRecord> FindPendingByOrderIds(IReadOnlyCollection<string> orderIds) =>
            inner.FindPendingByOrderIds(orderIds);
        public IReadOnlyList<ExecutionRecord> FindPendingCloses(string symbol, Market market, TradeSide closeSide) =>
            throw new InvalidOperationException("発注の記録を読めない（試験）");
        public bool RenewTracking(string orderId, DateTimeOffset trackedFrom) => inner.RenewTracking(orderId, trackedFrom);
        public bool UpdateOutcome(string orderId, OrderStatus status, int filledQuantity, decimal averagePrice,
            decimal slippageRatio, DateTimeOffset executedAt) =>
            inner.UpdateOutcome(orderId, status, filledQuantity, averagePrice, slippageRatio, executedAt);
    }

    private static Fixture NewFixture(int position = 1_428, Func<IExecutedOrderStore, IExecutedOrderStore>? wrapStore = null)
    {
        var clock = new MutableClock(T0);
        var broker = new FakeBroker { Position = position };
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var log = new RecordingLogger<SoftwareStopExecutor>();
        IExecutedOrderStore executorStore = wrapStore is null ? store : wrapStore(store);
        var reservations = new InMemoryOrderReservationStore();
        return new Fixture(
            new SoftwareStopExecutor(broker, broker, stops, executorStore, reservations, clock, log),
            broker, stops, store, clock, log, reservations);
    }

    // S1 の記録（エントリーは約定済み・未到達）。
    private static ProtectiveStopOrder S1(Fixture f, int quantity, decimal line, int minutesOld)
    {
        var id = Guid.NewGuid();
        var at = T0.AddMinutes(-minutesOld);
        var stop = new ProtectiveStopOrder(
            id, ProtectiveStopIds.SoftwareStopId(id), string.Empty, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, line, 1m, 0, ProtectiveStopState.Active, at, at,
            StopLossExecutionMethod.SoftwareStop);
        f.Stops.Save(stop);
        f.Store.Save(new ExecutionRecord(
            id, $"entry-{id:N}", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, quantity, 340m, quantity, 340m, OrderStatus.Filled, 0m, at));
        return stop;
    }

    // PoC と同じ 2 件（713 株が古く、ラインが高い）。
    private static (ProtectiveStopOrder Upper, ProtectiveStopOrder Lower) PocStops(Fixture f) =>
        (S1(f, 713, 331.67m, minutesOld: 120), S1(f, 715, 330.88m, minutesOld: 60));

    // 板に残っている処理中の決済（発注執行の非終端の Close の記録＋証券会社の生きている注文）。
    // #1222, IADR-0515: origin＝承認の出どころ（null＝分からない）。at＝記録の時刻（既定は到達の 25 秒前）。
    private static ExecutionRecord PendingClose(
        Fixture f, int quantity, Guid? decisionId = null, string? orderId = null, OrderApprovalOrigin? origin = null,
        DateTimeOffset? at = null)
    {
        var id = decisionId ?? Guid.NewGuid();
        var oid = orderId ?? $"order-{id:N}";
        var record = new ExecutionRecord(
            id, oid, "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, PositionEffect.Close,
            quantity, 331.67m, 0, 0m, OrderStatus.Accepted, 0m, at ?? T0.AddSeconds(-25), ApprovalOrigin: origin);
        f.Store.Save(record);
        f.Broker.Orders[oid] = new Order(quantity);
        return record;
    }

    // 713 株の記録（ライン 331.67）だけが到達する価格。
    private static StopLossTriggered Trigger(DateTimeOffset? at = null) =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 1_428, 331.50m, 331.67m, at ?? T0);

    private static Task<SoftwareStopCloseOutcome> Guard(Fixture f, ProtectiveStopOrder stop, IReadOnlyList<BrokerPositionSnapshot>? snapshot = null) =>
        f.Executor.TryCloseAsync(f.Stops.Find(stop.EntryDecisionId)!, snapshot);

    // T-10-1813: 判断の手仕舞い（全量 1,428 株・指値）が板にあるところへ S1 が発動する → 取り消してから 713 株の成行を送り、通る。
    [Fact]
    public async Task T_10_1813_判断の手仕舞いが処理中でも取り消してからS1の決済を送り損切りは止まらない()
    {
        var f = NewFixture();
        var (upper, lower) = PocStops(f);
        var decision = PendingClose(f, 1_428);

        var result = await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.Cancels.Should().Equal([decision.OrderId], "同じ建玉を売る判断の手仕舞いを取り消す");
        f.Broker.MarketCloses.Should().ContainSingle();
        f.Broker.MarketCloses[0].Status.Should().Be(OrderStatus.Accepted, "取り消した後に送るので建玉不足で拒否されない");
        f.Broker.MarketCloses[0].Intent.Quantity.Should().Be(713, "送るのは記録の残保護数量（判断の数量ではない）");
        result.Events.Should().ContainSingle().Which.Should().BeOfType<SoftwareStopExecuted>()
            .Which.Outcome.Should().Be(SoftwareStopOutcome.ClosePlaced);
        f.Stops.Find(upper.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        f.Stops.Find(lower.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active, "到達していない記録は動かない");
        f.Stops.Find(upper.EntryDecisionId)!.CloseFailures.Should().Be(0);
        f.Log.Entries.Should().Contain(e => e.Level == LogLevel.Warning
            && e.Message.Contains("判断の手仕舞いを取り消しました", StringComparison.Ordinal)
            && e.Message.Contains(decision.DecisionId.ToString(), StringComparison.Ordinal)
            && e.Message.Contains(decision.OrderId, StringComparison.Ordinal));
        // 記録は書かない（終端の記録・台帳の押さえの解放は約定追跡の OrderExecuted が行う）。出し直さない。
        f.Store.FindByDecisionId(decision.DecisionId)!.Status.Should().Be(OrderStatus.Accepted);
    }

    // T-10-1813（常駐ガードの経路）と、判断の手仕舞いが無い平常時は何も取り消さない（照会も増やさない）。
    [Fact]
    public async Task T_10_1813_常駐ガードの経路でも取り消してから送り判断の手仕舞いが無ければ何も取り消さない()
    {
        var f = NewFixture();
        var (upper, _) = PocStops(f);
        PendingClose(f, 1_428);
        // 到達だけ記録して決済は拒否させる（取消を止めた状態で 1 回）→ 待ち時間の後にガードが撃ち直す。
        f.Broker.CancelTakesEffect = false;
        await f.Executor.OnTriggeredAsync(Trigger());
        f.Broker.MarketCloses.Should().BeEmpty("取消が確定するまで送らない");

        f.Broker.CancelTakesEffect = true;
        var outcome = await Guard(f, upper, f.Broker.Snapshot());

        outcome.Event!.Outcome.Should().Be(SoftwareStopOutcome.ClosePlaced);
        f.Broker.MarketCloses.Should().ContainSingle().Which.Status.Should().Be(OrderStatus.Accepted);

        var plain = NewFixture();
        var (plainUpper, _) = PocStops(plain);
        await plain.Executor.OnTriggeredAsync(Trigger());
        plain.Broker.Cancels.Should().BeEmpty();
        plain.Broker.PositionQueries.Should().Be(1, "判断の手仕舞いが無ければ照会し直さない");
        plain.Stops.Find(plainUpper.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // T-10-1814: 窓（規則 11）の 2 つのプローブ。
    //   増える側: S1 が確かめた後・送る前に判断の手仕舞いが板に載る → 1 回拒否 → 次の撃ち直しで取り消して通る（止まらない）。
    //   減る側: 取消の最中に判断の手仕舞いが 1,000 株約定する → 建玉を照会し直し、減った分を売らない（二重に売らない）。
    [Fact]
    public async Task T_10_1814_窓の増える側は次の撃ち直しで取り消して通り減る側は減った分を売らない()
    {
        var grow = NewFixture();
        var (upper, _) = PocStops(grow);
        grow.Broker.BeforeMarketClose = () => PendingClose(grow, 1_428);

        await grow.Executor.OnTriggeredAsync(Trigger());
        grow.Broker.MarketCloses.Should().ContainSingle().Which.Status.Should().Be(OrderStatus.Rejected, "窓の中で載った分は 1 回拒否される");

        grow.Clock.UtcNow = grow.Stops.Find(upper.EntryDecisionId)!.NextCloseAttemptAt!.Value;
        var retried = await Guard(grow, upper);

        retried.Event!.Outcome.Should().Be(SoftwareStopOutcome.ClosePlaced, "次の撃ち直しが前の端で取り消して通る");
        grow.Broker.Cancels.Should().ContainSingle();
        grow.Broker.MarketCloses.Should().HaveCount(2);
        grow.Broker.MarketCloses[1].Status.Should().Be(OrderStatus.Accepted);

        var shrink = NewFixture();
        PocStops(shrink);
        PendingClose(shrink, 1_428);
        shrink.Broker.FillDuringCancel = 1_000;

        await shrink.Executor.OnTriggeredAsync(Trigger());

        shrink.Broker.Position.Should().Be(428);
        shrink.Broker.MarketCloses.Sum(c => c.Intent.Quantity)
            .Should().BeLessThanOrEqualTo(428, "取消の最中に約定した分を S1 が重ねて売りに出さない（送った数量で見る）");
        shrink.Broker.PositionQueries.Should().BeGreaterThanOrEqualTo(2, "取り消した後に建玉を照会し直す");
    }

    // T-10-1815: 🔴 保護の機構が出した決済は取り消さない —— S0 / S3 の保護レグ（Active・完了した記録の生きているレグ・StopOrderId だけで
    // 一致するもの）・他の S1 の決済・失効した逆指値の成行手仕舞い。建玉は十分にあり、S1 の決済は通る。
    [Fact]
    public async Task T_10_1815_保護レグと他のS1の決済と失効した逆指値の成行手仕舞いは取り消さない()
    {
        var f = NewFixture(position: 10_000);
        var (upper, lower) = PocStops(f);

        ProtectiveStopOrder LegRow(ProtectiveStopState state, StopLossExecutionMethod mechanism, int attempt)
        {
            var id = Guid.NewGuid();
            var row = new ProtectiveStopOrder(
                id, ProtectiveStopIds.StopDecisionId(id, attempt), $"leg-{id:N}", "AAPL", Market.UnitedStates, TradeSide.Buy,
                ProductType.Cash, BrokerProvider.MoomooSimulate, 100, 320m, 1m, attempt, state, T0.AddHours(-3), T0.AddHours(-3),
                mechanism);
            f.Stops.Save(row);
            return row;
        }

        var s0 = LegRow(ProtectiveStopState.Active, StopLossExecutionMethod.BrokerStopOrder, attempt: 1);
        var s3 = LegRow(ProtectiveStopState.Active, StopLossExecutionMethod.AlternativeBrokerOrderType, attempt: 1);
        var completedS0 = LegRow(ProtectiveStopState.Completed, StopLossExecutionMethod.BrokerStopOrder, attempt: 2);
        var s0ByOrderId = LegRow(ProtectiveStopState.Active, StopLossExecutionMethod.BrokerStopOrder, attempt: 1);

        var mechanical = new[]
        {
            PendingClose(f, 100, s0.StopDecisionId, s0.StopOrderId),
            PendingClose(f, 100, s3.StopDecisionId, s3.StopOrderId),
            PendingClose(f, 100, completedS0.StopDecisionId, completedS0.StopOrderId),
            // DecisionId は一致しないが OrderId が保護記録の StopOrderId と一致する（突合が採用したレグ）。
            PendingClose(f, 100, Guid.NewGuid(), s0ByOrderId.StopOrderId),
            // 失効した逆指値の成行手仕舞い（次の試行の DecisionId）。
            PendingClose(f, 100, ProtectiveStopIds.CloseDecisionId(s0.EntryDecisionId, 2)),
            // 他の S1 の記録の決済（受理・未約定）。
            PendingClose(f, 715, ProtectiveStopIds.SoftwareCloseDecisionId(lower.EntryDecisionId, 1)),
        };

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.Cancels.Should().BeEmpty("保護の機構が出した決済は取り消さない");
        mechanical.Should().OnlyContain(r => f.Broker.Orders[r.OrderId].Status == OrderStatus.Accepted);
        f.Broker.MarketCloses.Should().ContainSingle().Which.Status.Should().Be(OrderStatus.Accepted);
        f.Stops.Find(upper.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);

        // 判断の手仕舞いが混ざっていれば、それだけを取り消す。
        var mixed = NewFixture(position: 10_000);
        PocStops(mixed);
        var leg = PendingClose(mixed, 100);
        var legRow = new ProtectiveStopOrder(
            Guid.NewGuid(), leg.DecisionId, leg.OrderId, "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 100, 320m, 1m, 1, ProtectiveStopState.Active, T0, T0);
        mixed.Stops.Save(legRow);
        var decision = PendingClose(mixed, 1_428);

        await mixed.Executor.OnTriggeredAsync(Trigger());

        mixed.Broker.Cancels.Should().Equal([decision.OrderId]);
    }

    // T-10-1816: 取消が確定しない（取消の後も生きていると答える）→ 据え置く（送らない・失敗に数えない・待ち時間を置かない）。
    // 次の巡回で取消が確定すれば送る。
    [Fact]
    public async Task T_10_1816_取消が確定しないあいだは送らず失敗にも数えず次の巡回で確定すれば送る()
    {
        var f = NewFixture();
        var (upper, _) = PocStops(f);
        PendingClose(f, 1_428);
        f.Broker.CancelTakesEffect = false;

        var result = await f.Executor.OnTriggeredAsync(Trigger());

        result.Deferred.Should().Be(1);
        f.Broker.MarketCloses.Should().BeEmpty("取消が確定する前に送ると拒否される（押さえない証券会社では二重に売る）");
        var row = f.Stops.Find(upper.EntryDecisionId)!;
        row.CloseFailures.Should().Be(0, "据え置きは失敗ではない");
        row.NextCloseAttemptAt.Should().BeNull("待ち時間を置かない（次の巡回ですぐ確かめ直す）");
        row.TriggeredAt.Should().NotBeNull("到達の記録は残る");
        f.Log.Entries.Should().Contain(e => e.Level == LogLevel.Warning
            && e.Message.Contains("取消がまだ確定していません", StringComparison.Ordinal));

        f.Broker.CancelTakesEffect = true;
        f.Clock.UtcNow = T0.AddSeconds(30);
        var outcome = await Guard(f, upper);

        outcome.Event!.Outcome.Should().Be(SoftwareStopOutcome.ClosePlaced);
        f.Broker.Cancels.Should().HaveCount(2, "巡回ごとに取り消し直す");
        f.Broker.MarketCloses.Should().ContainSingle().Which.Status.Should().Be(OrderStatus.Accepted);
    }

    // T-10-1817: 確かめられない判断の手仕舞い（照会 null・例外）は取り消さず、従来どおり送る（環境の違う記録で S1 を永遠に待たせない）。
    // 取消が例外でも、照会で終端と分かれば送る。証券会社が既に終端と答えた注文は取り消さない。
    [Fact]
    public async Task T_10_1817_確かめられない判断の手仕舞いは取り消さずに送り取消の例外や終端は続行する()
    {
        foreach (var unknown in new[] { "null", "throws" })
        {
            var f = NewFixture();
            var (upper, _) = PocStops(f);
            var decision = PendingClose(f, 1_428);
            if (unknown == "null")
                f.Broker.QueryReturnsNull.Add(decision.OrderId);
            else
                f.Broker.QueryThrows.Add(decision.OrderId);

            await f.Executor.OnTriggeredAsync(Trigger());

            f.Broker.Cancels.Should().BeEmpty($"確かめられない（{unknown}）ものは取り消さない");
            f.Broker.MarketCloses.Should().ContainSingle($"確かめられない（{unknown}）ときは従来どおり送る（据え置かない）");
            f.Stops.Find(upper.EntryDecisionId)!.CloseFailures.Should().Be(1, "是正前と同じく拒否され、撃ち直しは続く");
        }

        var cancelThrows = NewFixture();
        PocStops(cancelThrows);
        PendingClose(cancelThrows, 1_428);
        cancelThrows.Broker.CancelThrows = true;
        await cancelThrows.Executor.OnTriggeredAsync(Trigger());
        cancelThrows.Broker.MarketCloses.Should().ContainSingle().Which.Status.Should().Be(OrderStatus.Accepted,
            "取消の例外は続行し、照会で終端と分かれば送る");

        var terminal = NewFixture();
        PocStops(terminal);
        var done = PendingClose(terminal, 1_428);
        terminal.Broker.Orders[done.OrderId].Status = OrderStatus.Cancelled;
        await terminal.Executor.OnTriggeredAsync(Trigger());
        terminal.Broker.Cancels.Should().BeEmpty("証券会社が既に終端と答えた注文は取り消さない");
        terminal.Broker.MarketCloses.Should().ContainSingle().Which.Status.Should().Be(OrderStatus.Accepted);
        terminal.Broker.PositionQueries.Should().Be(1, "約定が進んでいない終端では照会し直さない");
    }

    // T-10-1818: キャンセル（OperationCanceledException）は照会・取消の最中でも握り潰さずに伝播する。
    [Fact]
    public async Task T_10_1818_キャンセルは照会と取消の最中でも伝播する()
    {
        foreach (var at in new[] { "query", "cancel" })
        {
            var f = NewFixture();
            PocStops(f);
            var decision = PendingClose(f, 1_428);
            if (at == "query")
                f.Broker.QueryThrowsFor = id => id == decision.OrderId ? new OperationCanceledException() : null;
            else
                f.Broker.CancelThrowsFor = id => id == decision.OrderId ? new OperationCanceledException() : null;

            var act = () => f.Executor.OnTriggeredAsync(Trigger());

            await act.Should().ThrowAsync<OperationCanceledException>($"{at} の最中のキャンセル");
            f.Broker.MarketCloses.Should().BeEmpty();
        }
    }

    // T-10-1820: 常駐ガードの巡回のスナップショット（取消の前の建玉）で呼んでも、取り消したら照会し直す——
    // スナップショットを使い回すと、取消の最中に約定した分を S1 が重ねて売る。
    [Fact]
    public async Task T_10_1820_ガードのスナップショットで呼んでも取り消したら建玉を照会し直す()
    {
        var f = NewFixture();
        var (upper, _) = PocStops(f);
        // 到達だけ記録する（取消が確定しないので送らない）。
        PendingClose(f, 1_428);
        f.Broker.CancelTakesEffect = false;
        await f.Executor.OnTriggeredAsync(Trigger());
        f.Broker.MarketCloses.Should().BeEmpty();

        var staleSnapshot = f.Broker.Snapshot(); // 1,428 株（取消の前）
        f.Broker.CancelTakesEffect = true;
        f.Broker.FillDuringCancel = 1_000;
        var queriesBefore = f.Broker.PositionQueries;

        await Guard(f, upper, staleSnapshot);

        f.Broker.PositionQueries.Should().Be(queriesBefore + 1, "取り消した後はガードのスナップショットを捨てて照会し直す");
        f.Broker.MarketCloses.Sum(c => c.Intent.Quantity)
            .Should().BeLessThanOrEqualTo(428, "取消の最中に約定した分を重ねて売りに出さない（送った数量で見る）");
    }

    // T-10-1821: 🔴 保護逆指値を張れなかったエントリーの成行手仕舞い（エントリーの時点で出す・DecisionId＝エントリーから導出）は、
    // **保護記録が無くても**取り消さない（承認時の保護の文脈を書けなかった等）。取り消すと出し直されず、逆指値の無い建玉が残る。
    [Fact]
    public async Task T_10_1821_保護記録の無いエントリーの成行手仕舞いは取り消さない()
    {
        var f = NewFixture(position: 1_528);
        var (upper, _) = PocStops(f);
        var entryId = Guid.NewGuid();
        f.Store.Save(new ExecutionRecord(
            entryId, $"entry-{entryId:N}", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, 100, 332m, 100, 332m, OrderStatus.Filled, 0m, T0.AddSeconds(-30)));
        var unprotectedExit = PendingClose(f, 100, ProtectiveStopIds.CloseDecisionId(entryId, attempt: 1));
        f.Stops.Find(entryId).Should().BeNull("前提: このエントリーには保護記録が無い");

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.Cancels.Should().BeEmpty("保護逆指値を張れなかった建玉の成行手仕舞いは保護の機構が出した決済である");
        f.Broker.Orders[unprotectedExit.OrderId].Status.Should().Be(OrderStatus.Accepted);
        f.Broker.MarketCloses.Should().ContainSingle().Which.Status.Should().Be(OrderStatus.Accepted);
        f.Stops.Find(upper.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);

        // 別の銘柄・別の方向のエントリーから導いた DecisionId は見分けに使わない（判断の手仕舞いとして取り消す）。
        var other = NewFixture();
        PocStops(other);
        var shortEntry = Guid.NewGuid();
        other.Store.Save(new ExecutionRecord(
            shortEntry, $"entry-{shortEntry:N}", "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Open, 100, 332m, 100, 332m, OrderStatus.Filled, 0m, T0.AddSeconds(-30)));
        var lookalike = PendingClose(other, 1_428, ProtectiveStopIds.CloseDecisionId(shortEntry, attempt: 1));

        await other.Executor.OnTriggeredAsync(Trigger());

        other.Broker.Cancels.Should().Equal([lookalike.OrderId], "方向の違うエントリーの導出は同じ建玉の保護ではない");
    }

    // T-10-1823: 判断の手仕舞いが既に約定し切っている（証券会社は Filled と答え、発注の記録はまだ未約定のまま）。常駐ガードが
    // 巡回の先頭の建玉（約定の前）を渡す → 約定が記録より進んでいるので建玉を照会し直し、残りの建玉を超えて売りに出さない。
    [Fact]
    public async Task T_10_1823_既に約定し切った判断の手仕舞いは取り消さず建玉を照会し直して重ねて売らない()
    {
        var f = NewFixture();
        var (upper, _) = PocStops(f);
        var decision = PendingClose(f, 1_000);
        // 到達だけ記録する（取消が確定しないので送らない）。
        f.Broker.CancelTakesEffect = false;
        await f.Executor.OnTriggeredAsync(Trigger());
        f.Broker.MarketCloses.Should().BeEmpty();

        var staleSnapshot = f.Broker.Snapshot(); // 1,428 株（約定の前）
        var order = f.Broker.Orders[decision.OrderId];
        order.Filled = 1_000;
        order.Status = OrderStatus.Filled;
        f.Broker.Position -= 1_000;
        f.Store.FindByDecisionId(decision.DecisionId)!.FilledQuantity.Should().Be(0, "前提: 記録は約定追跡が書くまで古い");
        var cancelsBefore = f.Broker.Cancels.Count;
        var queriesBefore = f.Broker.PositionQueries;

        await Guard(f, upper, staleSnapshot);

        f.Broker.Cancels.Should().HaveCount(cancelsBefore, "終端の注文は取り消さない");
        f.Broker.PositionQueries.Should().Be(queriesBefore + 1, "約定が記録より進んでいれば建玉を照会し直す");
        f.Broker.MarketCloses.Sum(c => c.Intent.Quantity)
            .Should().BeLessThanOrEqualTo(428, "約定し切った分を重ねて売りに出さない（送った数量で見る）");
    }

    // T-10-1824: 判断の手仕舞いが 2 本。先の 1 本の取消が確定せず（生きている）、後の 1 本の取消は確定する →
    // 後の確定で「取消済み」に上書きしない。この巡回は送らない（据え置く）。
    [Fact]
    public async Task T_10_1824_取消が確定しない判断の手仕舞いが1本でも残れば他が確定しても送らない()
    {
        var f = NewFixture();
        var (upper, _) = PocStops(f);
        var stuck = PendingClose(f, 1_000);
        var cancelled = PendingClose(f, 428);
        f.Broker.CancelIgnored.Add(stuck.OrderId);

        var result = await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.Cancels.Should().Equal([stuck.OrderId, cancelled.OrderId]);
        f.Broker.Orders[cancelled.OrderId].Status.Should().Be(OrderStatus.Cancelled);
        result.Deferred.Should().Be(1);
        f.Broker.MarketCloses.Should().BeEmpty("取消が確定していない判断の手仕舞いが残るあいだは送らない");
        f.Stops.Find(upper.EntryDecisionId)!.CloseFailures.Should().Be(0);
    }

    // T-10-1825: 発注の記録を読めない（例外）→ 何も取り消さず、是正前と同じく S1 の決済を送る（損切りを読み出しの失敗で止めない）。
    [Fact]
    public async Task T_10_1825_処理中の決済を読めなければ取り消さずにS1の決済を送る()
    {
        var f = NewFixture(position: 10_000, wrapStore: inner => new PendingClosesThrowingStore(inner));
        var (upper, _) = PocStops(f);
        var decision = PendingClose(f, 1_428);

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.Cancels.Should().BeEmpty("読めないものは取り消さない");
        f.Broker.Orders[decision.OrderId].Status.Should().Be(OrderStatus.Accepted);
        f.Broker.MarketCloses.Should().ContainSingle().Which.Status.Should().Be(OrderStatus.Accepted);
        f.Stops.Find(upper.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        f.Log.Entries.Should().Contain(e => e.Level == LogLevel.Error
            && e.Message.Contains("処理中の判断の手仕舞いを読めませんでした", StringComparison.Ordinal));
    }

    // T-10-1826: 保護記録の StopDecisionId が (エントリー, 試行) からの導出と一致しない（建玉の乖離の採用などで付いた識別子）。
    // その DecisionId を持ち、注文番号は保護記録の StopOrderId と違う処理中の決済 → 保護の機構のものとして取り消さない。
    [Fact]
    public async Task T_10_1826_導出と一致しない保護記録のStopDecisionIdを持つ決済は取り消さない()
    {
        var f = NewFixture(position: 10_000);
        PocStops(f);
        var adoptedEntry = Guid.NewGuid();
        var adoptedStopId = Guid.NewGuid();
        var row = new ProtectiveStopOrder(
            adoptedEntry, adoptedStopId, "leg-adopted", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 100, 320m, 1m, 1, ProtectiveStopState.Active, T0.AddHours(-1), T0.AddHours(-1));
        f.Stops.Save(row);
        adoptedStopId.Should().NotBe(ProtectiveStopIds.StopDecisionId(adoptedEntry, 1), "前提: 導出と一致しない");
        adoptedStopId.Should().NotBe(ProtectiveStopIds.StopDecisionId(adoptedEntry, 2));
        var leg = PendingClose(f, 100, adoptedStopId, orderId: "leg-other-order");

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.Cancels.Should().BeEmpty("保護記録の StopDecisionId を持つ決済は保護レグである");
        f.Broker.Orders[leg.OrderId].Status.Should().Be(OrderStatus.Accepted);
        f.Broker.MarketCloses.Should().ContainSingle().Which.Status.Should().Be(OrderStatus.Accepted);
    }

    // ---- #1222, IADR-0515: 利用者の手仕舞い・維持率割れの自動縮小は取り消さず差し引く（T-10-2435..T-10-2440） ----

    // 利用者・自動縮小の決済が約定し切った（証券会社の注文が終端・建玉が減った・約定追跡が記録を終端にした）。
    private static void FillOwnerClose(Fixture f, ExecutionRecord record)
    {
        var order = f.Broker.Orders[record.OrderId];
        f.Broker.Position -= order.Quantity - order.Filled;
        order.Filled = order.Quantity;
        order.Status = OrderStatus.Filled;
        f.Store.UpdateOutcome(record.OrderId, OrderStatus.Filled, order.Quantity, 331m, 0m, f.Clock.UtcNow);
    }

    // T-10-2435（受け入れ基準 1）: 利用者の成行の手仕舞いが建玉の全量（1,428 株）を処理中 → S1 は取り消さず、残り 0 なので送らない
    // （据え置き・失敗に数えない・待ち時間を置かない）。利用者の決済が約定し切ると、減少は外部要因の観測に割り当てられ、S1 は 1 株も重ねて売らない。
    [Fact]
    public async Task T_10_2435_利用者の手仕舞いが全量を処理中ならS1は取り消さず送らず約定の後も重ねて売らない()
    {
        var f = NewFixture();
        var (upper, lower) = PocStops(f);
        var owner = PendingClose(f, 1_428, origin: OrderApprovalOrigin.OwnerClose);

        var result = await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.Cancels.Should().BeEmpty("利用者の手仕舞いは取り消さない");
        f.Broker.MarketCloses.Should().BeEmpty("処理中の決済を差し引いた残りが 0 なので送らない（二重に売らない）");
        result.Deferred.Should().Be(1);
        var row = f.Stops.Find(upper.EntryDecisionId)!;
        row.State.Should().Be(ProtectiveStopState.Active);
        row.TriggeredAt.Should().NotBeNull("到達の記録は残す（損切りを止めない）");
        row.CloseFailures.Should().Be(0, "失敗に数えない");
        row.NextCloseAttemptAt.Should().BeNull("待ち時間を置かない");
        f.Log.Entries.Should().Contain(e => e.Level == LogLevel.Warning
            && e.Message.Contains("すべて覆っています", StringComparison.Ordinal)
            && e.Message.Contains(owner.DecisionId.ToString(), StringComparison.Ordinal));

        FillOwnerClose(f, owner);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            f.Clock.UtcNow = f.Clock.UtcNow.AddSeconds(30);
            await Guard(f, upper, f.Broker.Snapshot());
        }

        f.Broker.MarketCloses.Should().BeEmpty("利用者が売り切った建玉を S1 が重ねて売らない");
        f.Broker.Cancels.Should().BeEmpty();
        // 建玉の減少は外部要因の観測として割り当てられる（記録を閉じる確定は常駐ガードの巡回の観測が行う。IADR-0344 追記(7)）。
        f.Stops.Find(upper.EntryDecisionId)!.EffectiveProtectedQuantity.Should().Be(0, "利用者が売った分を S1 の上限から外す");
        f.Stops.Find(lower.EntryDecisionId)!.TriggeredAt.Should().BeNull("到達していない記録は撃たない");
    }

    // T-10-2436（受け入れ基準 1）: 利用者の手仕舞いが一部（500 株・1,000 株）を処理中 → 取り消さず、建玉 − 処理中の残りを上限に送り、受理される。
    // 送る数量と利用者の決済の合計は建玉（1,428 株）を超えない。
    [Theory]
    [InlineData(500, 713)]
    [InlineData(1_000, 428)]
    public async Task T_10_2436_利用者の手仕舞いが一部を処理中なら取り消さず差し引いた残りだけを送る(int ownerQuantity, int expectedSent)
    {
        var f = NewFixture();
        var (upper, _) = PocStops(f);
        var owner = PendingClose(f, ownerQuantity, origin: OrderApprovalOrigin.OwnerClose);

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.Cancels.Should().BeEmpty("利用者の手仕舞いは取り消さない");
        f.Broker.Orders[owner.OrderId].Status.Should().Be(OrderStatus.Accepted);
        f.Broker.MarketCloses.Should().ContainSingle();
        f.Broker.MarketCloses[0].Status.Should().Be(OrderStatus.Accepted, "差し引いた残りなので建玉不足で拒否されない");
        f.Broker.MarketCloses[0].Intent.Quantity.Should().Be(expectedSent);
        (f.Broker.MarketCloses[0].Intent.Quantity + ownerQuantity).Should().BeLessThanOrEqualTo(1_428, "二重に売らない");
        f.Stops.Find(upper.EntryDecisionId)!.RemainingProtected.Should().Be(713 - expectedSent);
        f.Stops.Find(upper.EntryDecisionId)!.CloseFailures.Should().Be(0);
    }

    // T-10-2437（受け入れ基準 2）: 維持率割れの自動縮小の決済が処理中 → 利用者の手仕舞いと同じく取り消さず差し引く（一部・全量）。
    [Theory]
    [InlineData(1_000, 428)]
    [InlineData(1_428, 0)]
    public async Task T_10_2437_維持率割れの自動縮小が処理中なら取り消さず差し引いた残りだけを送る(int reductionQuantity, int expectedSent)
    {
        var f = NewFixture();
        PocStops(f);
        var reduction = PendingClose(f, reductionQuantity, origin: OrderApprovalOrigin.MaintenanceMarginReduction);

        var result = await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.Cancels.Should().BeEmpty("自動縮小は取り消さない");
        f.Broker.Orders[reduction.OrderId].Status.Should().Be(OrderStatus.Accepted);
        if (expectedSent == 0)
        {
            f.Broker.MarketCloses.Should().BeEmpty();
            result.Deferred.Should().Be(1);
        }
        else
        {
            f.Broker.MarketCloses.Should().ContainSingle().Which.Intent.Quantity.Should().Be(expectedSent);
            f.Broker.MarketCloses[0].Status.Should().Be(OrderStatus.Accepted);
        }
    }

    // T-10-2438（受け入れ基準 3・5）: 判断の手仕舞い（出どころ TradeDecision）と利用者の手仕舞い（200 株）が並ぶ → 判断の手仕舞いだけを
    // 取り消し、利用者の手仕舞いは残して差し引く。S1 は 713 株を送って受理される。
    [Fact]
    public async Task T_10_2438_判断の手仕舞いは従来どおり取り消し並ぶ利用者の手仕舞いは残して差し引く()
    {
        var f = NewFixture();
        var (upper, _) = PocStops(f);
        var decision = PendingClose(f, 1_228, origin: OrderApprovalOrigin.TradeDecision);
        var owner = PendingClose(f, 200, origin: OrderApprovalOrigin.OwnerClose);

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.Cancels.Should().Equal([decision.OrderId], "判断の手仕舞いだけを取り消す");
        f.Broker.Orders[owner.OrderId].Status.Should().Be(OrderStatus.Accepted);
        f.Broker.MarketCloses.Should().ContainSingle();
        f.Broker.MarketCloses[0].Status.Should().Be(OrderStatus.Accepted);
        f.Broker.MarketCloses[0].Intent.Quantity.Should().Be(713, "1,428 − 200 ≧ 713 なので縮めない");
        f.Stops.Find(upper.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // T-10-2439（受け入れ基準 4・否定形）: 🔴 出どころが分からない（null・Unknown）記録、および猶予（NettedCloseGrace）を過ぎても処理中の
    // 利用者の手仕舞いは、判断の手仕舞いと同じく取り消してから送る（損切りを止めない側。是正前と同じ）。
    [Theory]
    [InlineData("null")]
    [InlineData("unknown")]
    [InlineData("owner-stale")]
    [InlineData("reduction-stale")]
    public async Task T_10_2439_出どころが分からない決済と猶予を過ぎた利用者の決済は取り消してから送る(string shape)
    {
        var f = NewFixture();
        var (upper, _) = PocStops(f);
        var stale = T0 - SoftwareStopExecutor.NettedCloseGrace;
        var pending = shape switch
        {
            "null" => PendingClose(f, 1_428),
            "unknown" => PendingClose(f, 1_428, origin: OrderApprovalOrigin.Unknown),
            "owner-stale" => PendingClose(f, 1_428, origin: OrderApprovalOrigin.OwnerClose, at: stale),
            _ => PendingClose(f, 1_428, origin: OrderApprovalOrigin.MaintenanceMarginReduction, at: stale),
        };

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.Cancels.Should().Equal([pending.OrderId], "分からない・猶予を過ぎたものは取り消す側へ倒す");
        f.Broker.MarketCloses.Should().ContainSingle().Which.Status.Should().Be(OrderStatus.Accepted, "損切りは止まらない");
        f.Broker.MarketCloses[0].Intent.Quantity.Should().Be(713);
        f.Stops.Find(upper.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // T-10-2440（受け入れ基準 4・否定形）: 利用者の手仕舞いを確かめられない（照会 null・例外）／読み出しが例外 → 取り消さず、差し引かずに送る
    // （損切りを止めない）。差し引くための建玉照会が不明 → 据え置く（既存の「建玉不明は据え置き」と同じ・失敗に数えない）。
    [Theory]
    [InlineData("query-null")]
    [InlineData("query-throws")]
    [InlineData("read-throws")]
    [InlineData("positions-unknown")]
    public async Task T_10_2440_利用者の手仕舞いを確かめられなければ差し引かずに送り建玉が不明なら据え置く(string shape)
    {
        var f = shape == "read-throws"
            ? NewFixture(wrapStore: inner => new PendingClosesThrowingStore(inner))
            : NewFixture();
        var (upper, _) = PocStops(f);
        var owner = PendingClose(f, 500, origin: OrderApprovalOrigin.OwnerClose);
        if (shape == "query-null")
            f.Broker.QueryReturnsNull.Add(owner.OrderId);
        if (shape == "query-throws")
            f.Broker.QueryThrows.Add(owner.OrderId);
        if (shape == "positions-unknown")
            f.Broker.PositionsUnknownFrom = 2; // 1 回目（S1 の建玉照会）は答え、差し引きのための照会し直しが不明。

        var result = await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.Cancels.Should().BeEmpty("利用者の手仕舞いは取り消さない");
        if (shape == "positions-unknown")
        {
            f.Broker.MarketCloses.Should().BeEmpty();
            result.Deferred.Should().Be(1);
            f.Stops.Find(upper.EntryDecisionId)!.CloseFailures.Should().Be(0);
            return;
        }

        f.Broker.MarketCloses.Should().ContainSingle().Which.Intent.Quantity.Should().Be(713, "確かめられないものは差し引かない");
        f.Broker.MarketCloses[0].Status.Should().Be(OrderStatus.Accepted);
    }

    // ---- #1222 独立監査（R1・Y2）: T-10-2445..T-10-2447 ----

    // T-10-2445（R1・猶予の境界）: 利用者の手仕舞い 1,200 株が猶予の終わる 1 秒前に置かれ、判断の手仕舞い 228 株も処理中。判断の手仕舞いの照会の
    // 最中に時計が 3 秒進む（取消の段では猶予の内・差し引きの段では猶予の外）→ 🔴 利用者の手仕舞いは取り消さず、**差し引いて**残り 228 株だけを送る。
    // 是正前は差し引きの段が猶予を見直して素通りさせ、713 株を送った（押さえない証券会社では二重に売る）。
    [Fact]
    public async Task T_10_2445_試行の中で猶予をまたいだ利用者の手仕舞いも取り消さなければ必ず差し引く()
    {
        var f = NewFixture();
        PocStops(f);
        var decision = PendingClose(f, 228, origin: OrderApprovalOrigin.TradeDecision);
        var owner = PendingClose(
            f, 1_200, origin: OrderApprovalOrigin.OwnerClose, at: T0 - SoftwareStopExecutor.NettedCloseGrace + TimeSpan.FromSeconds(1));
        var advanced = false;
        f.Broker.QueryThrowsFor = id =>
        {
            if (id == decision.OrderId && !advanced)
            {
                advanced = true;
                f.Clock.UtcNow = f.Clock.UtcNow.AddSeconds(3);
            }

            return null;
        };

        await f.Executor.OnTriggeredAsync(Trigger());

        advanced.Should().BeTrue("前提: 判断の手仕舞いの照会の最中に猶予をまたぐ");
        f.Broker.Cancels.Should().Equal([decision.OrderId], "取消の段では猶予の内なので利用者の手仕舞いは取り消さない");
        f.Broker.Orders[owner.OrderId].Status.Should().Be(OrderStatus.Accepted);
        f.Broker.MarketCloses.Should().ContainSingle();
        f.Broker.MarketCloses[0].Intent.Quantity.Should().Be(228, "取り消さなかった生きている利用者の手仕舞いは必ず差し引く");
        f.Broker.MarketCloses[0].Status.Should().Be(OrderStatus.Accepted);
        (f.Broker.MarketCloses[0].Intent.Quantity + 1_200).Should().BeLessThanOrEqualTo(1_428, "二重に売らない");
    }

    // T-10-2446（Y2）: 利用者の手仕舞い 1,000 株のうち 600 株が約定済み（記録にも反映済み／記録はまだ 0 で証券会社だけが 600 と答える）。
    // 建玉は約定の後の 1,428 株。残り 400 株だけを処理中として引き、713 株を送る（数量の全量 1,000 で引くと 428 株に縮めてしまう）。
    [Theory]
    [InlineData(600)]
    [InlineData(0)]
    public async Task T_10_2446_一部約定した利用者の手仕舞いは約定の残りだけを差し引く(int recordedFilled)
    {
        var f = NewFixture();
        PocStops(f);
        var id = Guid.NewGuid();
        var oid = $"order-{id:N}";
        f.Store.Save(new ExecutionRecord(
            id, oid, "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, PositionEffect.Close,
            1_000, 331.67m, recordedFilled, recordedFilled > 0 ? 331m : 0m, OrderStatus.PartiallyFilled, 0m, T0.AddSeconds(-25),
            ApprovalOrigin: OrderApprovalOrigin.OwnerClose));
        f.Broker.Orders[oid] = new Order(1_000) { Status = OrderStatus.PartiallyFilled, Filled = 600 };

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.Cancels.Should().BeEmpty();
        f.Broker.MarketCloses.Should().ContainSingle().Which.Intent.Quantity.Should().Be(713, "1,428 − 残り 400 ≧ 713");
        f.Broker.MarketCloses[0].Status.Should().Be(OrderStatus.Accepted);
    }

    // T-10-2447（Y2）: 同じ銘柄の S1 の記録 2 件がともに到達し、利用者の手仕舞い 1,000 株が処理中。先に送った S1 の決済（生きている）も
    // 処理中の決済として引くので、2 件の合計は残りの 428 株を超えず、どちらも拒否されない。
    [Fact]
    public async Task T_10_2447_到達した2件のS1は先の決済も差し引き合計で建玉の残りを超えない()
    {
        var f = NewFixture();
        var (upper, lower) = PocStops(f);
        PendingClose(f, 1_000, origin: OrderApprovalOrigin.OwnerClose);

        await f.Executor.OnTriggeredAsync(
            new StopLossTriggered(Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 1_428, 330.50m, 330.88m, T0));

        f.Stops.Find(upper.EntryDecisionId)!.TriggeredAt.Should().NotBeNull("前提: 2 件とも到達した");
        f.Stops.Find(lower.EntryDecisionId)!.TriggeredAt.Should().NotBeNull("前提: 2 件とも到達した");

        f.Broker.Cancels.Should().BeEmpty("利用者の手仕舞いは取り消さない");
        f.Broker.MarketCloses.Should().NotBeEmpty();
        f.Broker.MarketCloses.Should().OnlyContain(c => c.Status == OrderStatus.Accepted, "差し引いた残りなので拒否されない");
        f.Broker.MarketCloses.Sum(c => c.Intent.Quantity).Should().Be(428, "建玉 1,428 − 利用者の手仕舞い 1,000");
    }

    // #1253, IADR-0515 追記(1): 利用者の手仕舞い（または自動縮小）の承認を予約したまま送信結果が不明になり、突合が証券会社の注文から
    // 発注済みと確定した記録を作る（発注執行の通常の経路を通らない）。placedAt＝証券会社が答えた発注の時刻（null＝答えない＝突合の時刻）。
    // 突合は記録が出来る前の時刻（到達の 30 秒前）に回り、注文は証券会社に生きている（受理・未約定）。
    private static async Task<ExecutionRecord> ReconciledClose(
        Fixture f, int quantity, OrderApprovalOrigin? reservedOrigin, DateTimeOffset? placedAt)
    {
        var decisionId = Guid.NewGuid();
        var orderId = $"reconciled-{decisionId:N}";
        f.Reservations.TryReserve(decisionId, T0.AddHours(-2), BrokerProvider.MoomooSimulate, approvalOrigin: reservedOrigin)
            .Should().BeTrue();
        var intent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, 331.67m,
            PositionEffect.Close, StopLossPrice: null);
        var brokerOrder = new BrokerOrder(orderId, intent, OrderStatus.Accepted, 0, 0m, placedAt ?? default, null);
        f.Broker.Orders[orderId] = new Order(quantity);
        var reconciler = new OrderReservationReconciler(
            f.Reservations, f.Store, new PlacedProbe(brokerOrder), f.Broker, f.Clock,
            Options.Create(new ReconciliationOptions { Enabled = true }));
        var now = f.Clock.UtcNow;
        f.Clock.UtcNow = T0.AddSeconds(-30);
        (await reconciler.ReconcileAsync(T0.AddHours(-1), batchSize: 10)).Terminalized.Should().Be(1);
        f.Clock.UtcNow = now;
        return f.Store.FindByDecisionId(decisionId)!;
    }

    private sealed class PlacedProbe(BrokerOrder order) : IReservationBrokerProbe
    {
        public Task<ReservationProbeResult> ProbeAsync(
            OrderDispatchReservation reservation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReservationProbeResult.Placed(order));
    }

    // T-10-2454（#1253 受け入れ基準 1・端から端まで）: 送信結果が不明だった利用者の手仕舞い（自動縮小）1,000 株を突合が発注済みと確定し
    // （証券会社は発注の時刻を答えない＝記録の時刻は突合の時刻）、30 秒後に S1 が到達する → 予約の行の出どころが記録へ運ばれているので
    // S1 は取り消さず、処理中の残りを差し引いた 428 株だけを送って受理される（#1222 の是正が突合の経路でも効く）。
    [Theory]
    [InlineData(OrderApprovalOrigin.OwnerClose)]
    [InlineData(OrderApprovalOrigin.MaintenanceMarginReduction)]
    public async Task T_10_2454_突合で確定した利用者の手仕舞いはS1に取り消されず差し引かれる(OrderApprovalOrigin origin)
    {
        var f = NewFixture();
        var (upper, _) = PocStops(f);
        var reconciled = await ReconciledClose(f, 1_000, origin, placedAt: null);
        reconciled.ApprovalOrigin.Should().Be(origin, "突合が予約の行の出どころを写す");
        reconciled.PositionEffect.Should().Be(PositionEffect.Close);

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.Cancels.Should().BeEmpty("突合で確定した利用者の手仕舞いも取り消さない");
        f.Broker.Orders[reconciled.OrderId].Status.Should().Be(OrderStatus.Accepted);
        f.Broker.MarketCloses.Should().ContainSingle();
        f.Broker.MarketCloses[0].Status.Should().Be(OrderStatus.Accepted, "差し引いた残りなので建玉不足で拒否されない");
        f.Broker.MarketCloses[0].Intent.Quantity.Should().Be(428, "1,428 − 1,000");
        f.Stops.Find(upper.EntryDecisionId)!.RemainingProtected.Should().Be(713 - 428);
    }

    // T-10-2455（#1253 受け入れ基準 2・否定形）: 🔴 出どころを持たない予約の行（列を足す前の行）から突合が確定した記録は null のままで、
    // S1 は従来どおり取り消してから 713 株を送る（推測で利用者の手仕舞いと読まない）。出どころを持っていても、証券会社が答えた発注の時刻が
    // 猶予（NettedCloseGrace）を過ぎていれば、通常の経路と同じく取り消してから送る（突合の経路だけ猶予を延ばさない）。
    [Theory]
    [InlineData("null-origin")]
    [InlineData("owner-past-grace")]
    public async Task T_10_2455_出どころの無い予約や猶予を過ぎた突合の記録はS1が取り消してから送る(string shape)
    {
        var f = NewFixture();
        var (upper, _) = PocStops(f);
        var reconciled = shape == "null-origin"
            ? await ReconciledClose(f, 1_428, reservedOrigin: null, placedAt: null)
            : await ReconciledClose(f, 1_428, OrderApprovalOrigin.OwnerClose, placedAt: T0.AddHours(-2));
        reconciled.ApprovalOrigin.Should().Be(shape == "null-origin" ? null : OrderApprovalOrigin.OwnerClose);

        await f.Executor.OnTriggeredAsync(Trigger());

        f.Broker.Cancels.Should().Equal([reconciled.OrderId], "分からない・猶予を過ぎたものは取り消す側へ倒す");
        f.Broker.MarketCloses.Should().ContainSingle().Which.Status.Should().Be(OrderStatus.Accepted, "損切りは止まらない");
        f.Broker.MarketCloses[0].Intent.Quantity.Should().Be(713);
        f.Stops.Find(upper.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }
}
