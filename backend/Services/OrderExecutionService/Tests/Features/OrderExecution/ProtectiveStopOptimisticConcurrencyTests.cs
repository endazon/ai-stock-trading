using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.AdoptPositionDrift;
using OrderExecutionService.Features.OrderExecution.AmendOrder;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 FR-10, FR-12, UC-02, ADR-0040 決定1（S1）, #833 項目3, IADR-0396:
// **保護記録の古い写しで、並行に進んだ新しい状態を上書きしない**（楽観並行の版番号・T-10-795..T-10-799）。
//
// 保護記録は到達ハンドラ・常駐ガード・約定追跡（再武装）・乖離の取り込みが別々のスコープから並行に書く。
// かつての Save は古い写しの全列を書き戻す last-writer-wins で、巻き戻った試行番号が同じ決済を「記録済み」と読ませ、
// **帳簿を二度減らして ClosePlaced を二度出した**。並行の割り込みは偽物の建玉照会・発注記録の保存に差し込んで再現する
// （sleep や実スレッドの競合に頼らない）。時計は注入した固定値。配置は稼働 PoC と同じ AAPL。
public class ProtectiveStopOptimisticConcurrencyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 15, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // 成行決済・建玉照会・注文照会を持つブローカー。建玉照会に割り込み（並行する別の経路）を差し込める。
    private sealed class FakeBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; }

        public Action? OnPositionsQueried { get; set; }

        public List<(OrderIntent Intent, Guid DecisionId)> MarketCloses { get; } = [];

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("本テストは通常発注を行わない");

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            throw new NotSupportedException("S1 はブローカーへ逆指値を出さない");

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCloses.Add((closeIntent, decisionId));
            return Task.FromResult(new BrokerOrder(
                $"close-{MarketCloses.Count}", closeIntent, OrderStatus.Accepted, 0, 0m, Now, null));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default)
        {
            var hook = OnPositionsQueried;
            OnPositionsQueried = null; // 割り込みは 1 回だけ
            hook?.Invoke();
            return Task.FromResult(Positions);
        }
    }

    // 発注記録の保存の直後に割り込みを差し込める記録ストア（送信後・行の確定前の窓を再現する）。
    private sealed class HookedExecutedOrderStore : IExecutedOrderStore
    {
        private readonly InMemoryExecutedOrderStore _inner = new();

        public Action<ExecutionRecord>? AfterSave { get; set; }

        public void Save(ExecutionRecord record)
        {
            _inner.Save(record);
            var hook = AfterSave;
            AfterSave = null;
            hook?.Invoke(record);
        }

        public ExecutionRecord? FindByDecisionId(Guid decisionId) => _inner.FindByDecisionId(decisionId);

        public IReadOnlyList<ExecutionRecord> GetAll() => _inner.GetAll();

        public IReadOnlyList<ExecutionRecord> FindPendingSince(DateTimeOffset since, int batchSize) =>
            _inner.FindPendingSince(since, batchSize);

        public bool UpdateOutcome(
            string orderId, OrderStatus status, int filledQuantity, decimal averagePrice, decimal slippageRatio,
            DateTimeOffset executedAt) =>
            _inner.UpdateOutcome(orderId, status, filledQuantity, averagePrice, slippageRatio, executedAt);
    }

    // 指定した行への楽観並行の保存を、指定回数（int.MaxValue なら常に）衝突させるストア。
    private sealed class ConflictingStopStore(InMemoryProtectiveStopOrderStore inner, Guid target, int conflicts)
        : IProtectiveStopOrderStore
    {
        private int _remaining = conflicts;

        public int Conflicts { get; private set; }

        public void Save(ProtectiveStopOrder stop) => inner.Save(stop);

        public bool TrySave(ProtectiveStopOrder stop)
        {
            if (stop.EntryDecisionId == target && _remaining > 0)
            {
                _remaining--;
                Conflicts++;
                return false;
            }

            return inner.TrySave(stop);
        }

        public ProtectiveStopOrder? Find(Guid entryDecisionId) => inner.Find(entryDecisionId);

        public IReadOnlyList<ProtectiveStopOrder> FindActive(int batchSize) => inner.FindActive(batchSize);

        public IReadOnlyList<ProtectiveStopOrder> FindActiveSoftwareStops(string symbol, Market market, TradeSide entrySide) =>
            inner.FindActiveSoftwareStops(symbol, market, entrySide);

        public IReadOnlyList<ProtectiveStopOrder> FindCompletedSoftwareStops(
            string symbol, Market market, TradeSide entrySide, int limit) =>
            inner.FindCompletedSoftwareStops(symbol, market, entrySide, limit);

        // FR-10, #880, IADR-0412 決定2: 帰属不明の通知済みの印を持つ行。
        public IReadOnlyList<ProtectiveStopOrder> FindUnattributedNotified(int limit) =>
            inner.FindUnattributedNotified(limit);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IEnumerable<string> Errors => Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message);

        public IEnumerable<string> Warnings => Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static BrokerPositionSnapshot Long(int qty) => new("AAPL", Market.UnitedStates, qty, 340m);

    // 到達済みの S1 の行（エントリーは約定済み・残保護数量は確定済み）とエントリーの発注記録を置く。
    private static ProtectiveStopOrder TriggeredStop(
        IProtectiveStopOrderStore stops, IExecutedOrderStore store, int quantity, decimal line = 338.51m, int minutesOld = 360)
    {
        var id = Guid.NewGuid();
        var at = Now.AddMinutes(-minutesOld);
        var stop = new ProtectiveStopOrder(
            id, ProtectiveStopIds.SoftwareStopId(id), string.Empty, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, line, 1m, 0, ProtectiveStopState.Active, at, at,
            StopLossExecutionMethod.SoftwareStop, TriggeredAt: Now.AddMinutes(-1), TriggeredPrice: 338.20m,
            RemainingProtected: quantity, LastTriggerSeenAt: Now.AddMinutes(-1));
        stops.Save(stop);
        store.Save(new ExecutionRecord(
            id, $"entry-{id:N}", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, quantity, 340m, quantity, 340m, OrderStatus.Filled, 0m, at));
        return stops.Find(id)!;
    }

    private static int ClosePlacedCount(IEnumerable<SoftwareStopExecuted?> events) =>
        events.Count(e => e is { Outcome: SoftwareStopOutcome.ClosePlaced });

    // T-10-795: 版の契約。**古い写しは書かれず（false）、読み直した写しは書ける。無条件の保存は古い追跡でも通る。**
    // 本番の EF ストア（並行トークン）と試験・paper 構成のインメモリストアの両方で同じ。
    [Fact]
    public void T_10_795_古い写しの楽観並行の保存は書かれず読み直せば書ける_EFとインメモリの両方()
    {
        // --- EF（本番のストア。別スコープ＝別コンテキストで同じ行を書く） ---
        var dbName = Guid.NewGuid().ToString();
        OrderExecutionDbContext NewContext() =>
            new(new DbContextOptionsBuilder<OrderExecutionDbContext>().UseInMemoryDatabase(dbName).Options);

        using var guardScope = NewContext();
        using var handlerScope = NewContext();
        var guard = new EfProtectiveStopOrderStore(guardScope);
        var handler = new EfProtectiveStopOrderStore(handlerScope);
        var stop = TriggeredStop(guard, new InMemoryExecutedOrderStore(), 707);
        stop.Version.Should().Be(0);

        var staleCopy = guard.Find(stop.EntryDecisionId)!; // ガードが巡回の先頭で読んだ写し（版 0）

        // ハンドラが先に決済を確定した（Completed・試行 1）。
        var handlerCopy = handler.Find(stop.EntryDecisionId)!;
        handler.TrySave(handlerCopy with { State = ProtectiveStopState.Completed, RemainingProtected = 0, Attempt = 1 })
            .Should().BeTrue();

        // 🔴 ガードが古い写しで Active を書き戻そうとする → 書かれない。
        guard.TrySave(staleCopy with { PendingExternalReduction = 3 }).Should().BeFalse();
        using (var probe = NewContext())
        {
            var persisted = new EfProtectiveStopOrderStore(probe).Find(stop.EntryDecisionId)!;
            persisted.State.Should().Be(ProtectiveStopState.Completed, "古い写しで完了を巻き戻さない");
            persisted.Attempt.Should().Be(1, "古い写しで試行番号を巻き戻さない");
            persisted.PendingExternalReduction.Should().Be(0);
            persisted.Version.Should().Be(1);
        }

        // 失敗の後の Find は保存先の最新を返し（追跡が外れている）、そこから作った写しは書ける。
        var fresh = guard.Find(stop.EntryDecisionId)!;
        fresh.Version.Should().Be(1);
        fresh.State.Should().Be(ProtectiveStopState.Completed);
        guard.TrySave(fresh with { StalledNotifiedAt = Now }).Should().BeTrue();

        // 同じコンテキストの中でも、自分が書いた後に古い写し（版 2 より前）で書こうとすれば書かれない。
        guard.TrySave(fresh with { PendingExternalReduction = 9 }).Should().BeFalse("版 1 の写しは版 2 の行を上書きしない");
        guard.Find(stop.EntryDecisionId)!.PendingExternalReduction.Should().Be(0);
        guard.Find(stop.EntryDecisionId)!.StalledNotifiedAt.Should().Be(Now);

        // コンテキストは汚れていない（別の行の保存が失敗した更新を巻き込まない）。
        var other = TriggeredStop(guard, new InMemoryExecutedOrderStore(), 10);
        guard.Find(other.EntryDecisionId).Should().NotBeNull();

        // 無条件の保存（常駐ガードの S0 経路が使う）は、追跡している写しが古くても通り、版を進める。
        var handlerTracked = handler.Find(stop.EntryDecisionId)!; // handler の追跡は版 1 のまま（古い）
        handlerTracked.Version.Should().Be(1);
        handler.Save(handlerTracked with { UpdatedAt = Now.AddMinutes(1) });
        using (var probe = NewContext())
        {
            var persisted = new EfProtectiveStopOrderStore(probe).Find(stop.EntryDecisionId)!;
            persisted.Version.Should().Be(3);
            persisted.UpdatedAt.Should().Be(Now.AddMinutes(1));
        }

        // --- インメモリ（paper 構成・単体試験）も同じ契約 ---
        var memory = new InMemoryProtectiveStopOrderStore();
        var row = TriggeredStop(memory, new InMemoryExecutedOrderStore(), 707);
        memory.TrySave(row with { Attempt = 1 }).Should().BeTrue();
        memory.TrySave(row with { Attempt = 5 }).Should().BeFalse("版 0 の写しは版 1 の行を上書きしない");
        memory.Find(row.EntryDecisionId)!.Attempt.Should().Be(1);
        memory.Find(row.EntryDecisionId)!.Version.Should().Be(1);
        memory.Save(row with { Attempt = 2 });
        memory.Find(row.EntryDecisionId)!.Version.Should().Be(2, "無条件の保存も版を進める");
    }

    // T-10-796: 🔴 ハンドラとガードが**同じ試行**を確定しに行っても、帳簿の減算と ClosePlaced は 1 回だけ。
    // 送信 → 発注記録の保存 → （ここでガードが「記録済み」を見て先に確定）→ ハンドラの確定、の窓を再現する。
    [Fact]
    public async Task T_10_796_同じ試行をハンドラとガードが確定してもClosePlacedと減算は1回だけ()
    {
        var broker = new FakeBroker { Positions = [Long(707)] };
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new HookedExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var handlerLog = new RecordingLogger<SoftwareStopExecutor>();
        var handler = new SoftwareStopExecutor(broker, broker, stops, store, reservations, new FakeClock(), handlerLog);
        var guard = new SoftwareStopExecutor(broker, broker, stops, store, reservations, new FakeClock());
        var stop = TriggeredStop(stops, store, 707);

        SoftwareStopCloseOutcome? guardOutcome = null;
        store.AfterSave = record =>
        {
            if (record.PositionEffect != PositionEffect.Close)
                return;
            // ガードの巡回（行はまだ試行 0・Active）が、記録済みの決済を見つけて確定する。
            guardOutcome = guard.TryCloseAsync(stops.Find(stop.EntryDecisionId)!, snapshot: null).GetAwaiter().GetResult();
        };

        var handlerOutcome = await handler.TryCloseAsync(stop, snapshot: null);

        broker.MarketCloses.Should().ContainSingle("成行は 1 本だけ（予約・記録の再送防止）");
        guardOutcome!.Event!.Outcome.Should().Be(SoftwareStopOutcome.ClosePlaced, "記録を見つけた側が確定する");
        ClosePlacedCount([guardOutcome.Event, handlerOutcome.Event])
            .Should().Be(1, "🔴 同じ試行の ClosePlaced を二度出さない（台帳の押さえ・通知が重なる）");
        handlerLog.Warnings.Should().Contain(m => m.Contains("別の経路が既に確定しています"));

        var row = stops.Find(stop.EntryDecisionId)!;
        row.State.Should().Be(ProtectiveStopState.Completed);
        row.Attempt.Should().Be(1);
        row.RemainingProtected.Should().Be(0);
    }

    // T-10-797: 🔴 乖離の取り込みが await を跨いで持っていた古い写しで、並行に完了した行を **Active・試行 0 へ巻き戻さない**。
    // 巻き戻ると、次の巡回が同じ試行を「記録済み」と読んで帳簿を二度減らし、ClosePlaced を二度出す。
    [Fact]
    public async Task T_10_797_取り込みの古い写しで完了した行を巻き戻さず決済が二度確定しない()
    {
        var broker = new FakeBroker();
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var executorBroker = new FakeBroker { Positions = [Long(707)] };
        var executor = new SoftwareStopExecutor(
            executorBroker, executorBroker, stops, store, reservations, new FakeClock());
        var adopterLog = new RecordingLogger<ProtectiveStopDriftAdopter>();
        var adopter = new ProtectiveStopDriftAdopter(
            stops, new OrderAmendmentService(broker, store, new InMemoryOrderLifecycleStore(), new FakeClock()),
            new FakeClock(), adopterLog, positions: broker);
        var stop = TriggeredStop(stops, store, 707);

        // 取り込みが建玉を照会しているあいだに、決済経路が 707 株の成行を確定して行を完了させる。
        var events = new List<SoftwareStopExecuted?>();
        broker.Positions = [Long(300)];
        broker.OnPositionsQueried = () =>
            events.Add(executor.TryCloseAsync(stops.Find(stop.EntryDecisionId)!, snapshot: null).GetAwaiter().GetResult().Event);

        var adoption = await adopter.ApplyAsync(new PositionDriftAdopted(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, 707, 300, 300, Now.AddMinutes(-5), 340m,
            RealizedPnlRecorded: false, ReferencePrice: null, EstimatedPnlInBase: null,
            Actor: "owner", Reason: "test", AdoptedAt: Now));

        adoption.Events.OfType<SoftwareStopExecuted>().Should().BeEmpty("減らしていない主張を「減らした」と言わない");
        adopterLog.Warnings.Should().Contain(m => m.Contains("並行に更新されていたため主張を減らしませんでした"));
        var row = stops.Find(stop.EntryDecisionId)!;
        row.State.Should().Be(ProtectiveStopState.Completed, "🔴 古い写しで Active へ巻き戻さない");
        row.Attempt.Should().Be(1, "🔴 古い写しで試行番号を巻き戻さない");

        // 次の巡回: 完了した行は対象外。同じ試行を「記録済み」と読み直して二度確定することは無い。
        events.Add((await executor.TryCloseAsync(stops.Find(stop.EntryDecisionId)!, snapshot: null)).Event);
        ClosePlacedCount(events).Should().Be(1);
        executorBroker.MarketCloses.Should().ContainSingle();
    }

    // T-10-798: 巡回の割り当て（観測の記録）・帰属不明の通知が**古い群の写し**で書くとき、並行に完了した行を巻き戻さず、
    // 書けなかった変化について通知もしない。
    [Fact]
    public void T_10_798_割り当てと帰属不明の通知は古い群の写しで完了した行を巻き戻さない()
    {
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var stop = TriggeredStop(stops, store, 707);
        var staleGroup = stops.FindActive(500); // 決済経路が await の前に読んだ群

        // 並行に別の経路が決済を確定して行を完了させた（版が進む）。
        stops.TrySave(stops.Find(stop.EntryDecisionId)! with
        {
            State = ProtectiveStopState.Completed,
            RemainingProtected = 0,
            Attempt = 1,
        }).Should().BeTrue();

        // 建玉照会は 300 株（主張 707 を下回る）＝外部要因の観測を積もうとする。古い群で書きに行く。
        var group = ProtectiveStopNetting.ReconcileShares(
            "AAPL", Market.UnitedStates, TradeSide.Buy, [Long(300)], staleGroup, stops, store, Now);

        var row = stops.Find(stop.EntryDecisionId)!;
        row.State.Should().Be(ProtectiveStopState.Completed, "🔴 古い群の写しで Active へ巻き戻さない");
        row.PendingExternalReduction.Should().Be(0);
        row.Attempt.Should().Be(1);
        group.Should().ContainSingle().Which.State.Should().Be(ProtectiveStopState.Completed, "書けなかった行は最新へ差し替わる");

        // 帰属不明の通知: 代表行の古い写しで印を書けなければ鳴らさない（印の無いまま鳴らすと次の巡回で重ねて鳴る）。
        var events = new List<object>();
        ProtectiveStopNetting.DetectUnattributedPositions([Long(1_000)], staleGroup, stops, store, Now, events);
        events.Should().BeEmpty();
        stops.Find(stop.EntryDecisionId)!.UnattributedNotifiedAt.Should().BeNull();

        // 最新の群では鳴る（次の巡回）。
        ProtectiveStopNetting.DetectUnattributedPositions([Long(1_000)], stops.FindActive(500), stops, store, Now, events);
        events.OfType<SoftwareStopExecuted>().Should().ContainSingle()
            .Which.Outcome.Should().Be(SoftwareStopOutcome.UnattributedPosition);
    }

    // T-10-798b: 決済経路が建玉照会を待つあいだに行が完了したら撃たない。観測を書けなくても、観測前の主張で撃たない。
    [Fact]
    public async Task T_10_798b_決済経路は照会を待つあいだに完了した行を撃たず書けなかった観測でも数量を縮める()
    {
        // (a) 建玉照会のあいだに別の経路が行を完了させた（建玉は 0）。古い写しの 707 株で成行を出すと裸の売りになる。
        var broker = new FakeBroker { Positions = [] };
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var executor = new SoftwareStopExecutor(
            broker, broker, stops, store, new InMemoryOrderReservationStore(), new FakeClock());
        var stop = TriggeredStop(stops, store, 707);
        broker.OnPositionsQueried = () => stops.TrySave(stops.Find(stop.EntryDecisionId)! with
        {
            State = ProtectiveStopState.Completed,
            RemainingProtected = 0,
        });

        var completedMeanwhile = await executor.TryCloseAsync(stop, snapshot: null);

        completedMeanwhile.Kind.Should().Be(SoftwareStopCloseKind.NotApplicable);
        broker.MarketCloses.Should().BeEmpty("🔴 並行に完了した行を古い写しで撃たない");
        stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);

        // (b) 建玉は 300 株（主張 707）。観測（超過 407）の保存が衝突しても、この回の成行は 300 株に縮める。
        var inner = new InMemoryProtectiveStopOrderStore();
        var store2 = new InMemoryExecutedOrderStore();
        var row = TriggeredStop(inner, store2, 707);
        var conflicting = new ConflictingStopStore(inner, row.EntryDecisionId, conflicts: 1);
        var broker2 = new FakeBroker { Positions = [Long(300)] };
        var executor2 = new SoftwareStopExecutor(
            broker2, broker2, conflicting, store2, new InMemoryOrderReservationStore(), new FakeClock());

        await executor2.TryCloseAsync(inner.Find(row.EntryDecisionId)!, snapshot: null);

        conflicting.Conflicts.Should().Be(1, "観測の保存が 1 回衝突した");
        broker2.MarketCloses.Should().ContainSingle().Which.Intent.Quantity
            .Should().Be(300, "🔴 書けなかった観測でも、この回の数量は建玉に見合う量に縮める（建玉より多く売らない）");
    }

    // T-10-799: 衝突し続けた行は**書かずに Critical ログを出して据え置き**、同じ到達の他の行（AAPL 715 株 / 713 株の 2 行）の
    // 決済とイベントは失わない。再武装は衝突しても読み直して当て直す（記録は既に終端化済みで、諦めると二度と起きない）。
    [Fact]
    public async Task T_10_799_衝突し続けた行は据え置いて声に出し他の行の決済は失わず再武装は当て直す()
    {
        var broker = new FakeBroker { Positions = [Long(715 + 713)] };
        var inner = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var a = TriggeredStop(inner, store, 715, line: 330.88m, minutesOld: 400);
        var b = TriggeredStop(inner, store, 713, line: 331.67m, minutesOld: 390);
        var stops = new ConflictingStopStore(inner, b.EntryDecisionId, int.MaxValue);
        var log = new RecordingLogger<SoftwareStopExecutor>();
        var executor = new SoftwareStopExecutor(
            broker, broker, stops, store, new InMemoryOrderReservationStore(), new FakeClock(), log);

        var result = await executor.OnTriggeredAsync(new StopLossTriggered(
            Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 1_428, 329m, 331.67m, Now));

        // A は決済され、そのイベントは残る。B は書けないので送らず、据え置いて声に出す。
        result.Matched.Should().Be(2);
        result.Deferred.Should().Be(1);
        result.Events.OfType<SoftwareStopExecuted>().Should().ContainSingle()
            .Which.EntryDecisionId.Should().Be(a.EntryDecisionId);
        broker.MarketCloses.Should().ContainSingle().Which.Intent.Quantity.Should().Be(715);
        log.Errors.Should().Contain(m => m.Contains("並行更新と衝突し続けました") && m.Contains(b.EntryDecisionId.ToString()));
        inner.Find(b.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active, "書いていない（古い写しで上書きしない）");

        // ガードの入口: 送る前に書くものが無いので B の成行は出る（出口を塞がない）。確定の保存だけが衝突し続けるが、
        // 例外は投げずに据え置いて声に出す。発注記録は残っているので、次の巡回は**再送せず**記録の結果で確定しに行く。
        var guarded = await executor.TryCloseAsync(inner.Find(b.EntryDecisionId)!, snapshot: null);
        guarded.Kind.Should().Be(SoftwareStopCloseKind.Deferred);
        broker.MarketCloses.Should().HaveCount(2);
        broker.MarketCloses[1].Intent.Quantity.Should().Be(713);
        store.FindByDecisionId(ProtectiveStopIds.SoftwareCloseDecisionId(b.EntryDecisionId, 1)).Should().NotBeNull();
        var again = await executor.TryCloseAsync(inner.Find(b.EntryDecisionId)!, snapshot: null);
        again.Kind.Should().Be(SoftwareStopCloseKind.Deferred);
        broker.MarketCloses.Should().HaveCount(2, "🔴 行を書けなくても、記録済みの試行は再送しない（二重に売らない）");
        log.Errors.Count(m => m.Contains("並行更新と衝突し続けました")).Should().BeGreaterThanOrEqualTo(3);

        // 再武装: 2 回衝突しても読み直して当て直し、未約定の 715 株を A へ戻す。
        var reArmStops = new ConflictingStopStore(inner, a.EntryDecisionId, conflicts: 2);
        var reArmer = new SoftwareStopReArmer(reArmStops, store, new FakeClock());
        var aLeg = store.FindByDecisionId(ProtectiveStopIds.SoftwareCloseDecisionId(a.EntryDecisionId, 1))!;
        reArmer.OnCloseTerminalized(aLeg, OrderStatus.Expired, filledQuantity: 0)!
            .Outcome.Should().Be(SoftwareStopOutcome.CloseUnfilled);
        reArmStops.Conflicts.Should().Be(2);
        var reArmed = inner.Find(a.EntryDecisionId)!;
        reArmed.State.Should().Be(ProtectiveStopState.Active);
        reArmed.RemainingProtected.Should().Be(715);
    }
}
