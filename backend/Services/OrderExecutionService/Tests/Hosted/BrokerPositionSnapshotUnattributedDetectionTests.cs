using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Features.OrderExecution.ObserveBrokerPositions;
using OrderExecutionService.Hosted;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 FR-10, UC-02, ADR-0040 決定1（S1）, #880, IADR-0412: 帰属不明の建玉の検知を建玉観測の常駐へ相乗りさせる。
//
// 起点は #820 の 11 巡目監査の 2 件である。
//   NB-2: 常駐ガードは Active な保護記録が 0 件の巡回では建玉を照会しないため、受理後に 0 約定で取り消された決済の残りが
//         その口座で唯一の S1 の痕跡なら検知が一度も走らない（1 銘柄しか持たない PoC 口座で無音。監査 PROBE1）。
//   NB-1: 検知の走査が建玉スナップショットの非 0 の群だけを回すため、純額 0 の巡回で通知済みの印がリセットされず、
//         再発した同数の帰属不明が 60 分黙る（監査 PROBE2c）。
//
// 時刻はすべて注入時計で進める（壁時計の待ちを使わない）。
public class BrokerPositionSnapshotUnattributedDetectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 15, 0, 0, TimeSpan.Zero);

    private const string ServiceName = "ai-stock-trading.order-execution-service";

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = T0;
    }

    private sealed class FuncClock(Func<DateTimeOffset> now) : IClock
    {
        public DateTimeOffset UtcNow => now();
    }

    private sealed class FuncTimeProvider(Func<DateTimeOffset> now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now();
    }

    // 建玉照会の回数を数える（相乗り＝往復を増やさないことの証拠）。
    private sealed class FakePositionSource : IBrokerPositionSource
    {
        public IReadOnlyList<BrokerPositionSnapshot>? Result { get; set; } = [];

        public int Calls { get; private set; }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    // ガードに渡すブローカー。建玉照会の回数を数える（注文照会・発注は本テストでは起きない）。
    private sealed class GuardBroker : IBrokerAdapter, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [];

        public int PositionQueries { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("本テストは発注しない");

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) =>
            throw new NotSupportedException("本テストは取り消さない");

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default)
        {
            PositionQueries++;
            return Task.FromResult(Positions);
        }
    }

    // 巡回対象の読み出しで例外を投げるストア（検知の失敗が観測の発行を巻き戻さないことを見る）。
    private sealed class ThrowingFindActiveStore(IProtectiveStopOrderStore inner) : IProtectiveStopOrderStore
    {
        public void Save(ProtectiveStopOrder stop) => inner.Save(stop);

        public bool TrySave(ProtectiveStopOrder stop) => inner.TrySave(stop);

        public ProtectiveStopOrder? Find(Guid entryDecisionId) => inner.Find(entryDecisionId);

        public IReadOnlyList<ProtectiveStopOrder> FindActive(int batchSize) =>
            throw new InvalidOperationException("保護記録を読めない（テスト）");

        public IReadOnlyList<ProtectiveStopOrder> FindActiveSoftwareStops(string symbol, Market market, TradeSide entrySide) =>
            inner.FindActiveSoftwareStops(symbol, market, entrySide);

        public IReadOnlyList<ProtectiveStopOrder> FindCompletedSoftwareStops(
            string symbol, Market market, TradeSide entrySide, int limit) =>
            inner.FindCompletedSoftwareStops(symbol, market, entrySide, limit);

        public IReadOnlyList<ProtectiveStopOrder> FindUnattributedNotified(int limit) =>
            inner.FindUnattributedNotified(limit);
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

    /// <summary>
    /// 常駐が巡回ごとに作るスコープ（本番は Program.cs の scoped 登録）。検知の部品だけを持つ最小の組み立て。
    /// </summary>
    internal static IServiceScopeFactory DetectorScopes(
        IProtectiveStopOrderStore stops, IExecutedOrderStore store, Func<DateTimeOffset> now) =>
        new ServiceCollection()
            .AddScoped(_ => new UnattributedPositionDetector(stops, store, new FuncClock(now), batchSize: 50))
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    private static Task<IHost> NewHostAsync() =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private sealed record Fixture(
        IHost Host,
        BrokerPositionSnapshotService Service,
        FakePositionSource Source,
        FakeClock Clock,
        InMemoryProtectiveStopOrderStore Stops,
        InMemoryExecutedOrderStore Store,
        RecordingLogger<BrokerPositionSnapshotService> Logger) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync();
            Host.Dispose();
        }
    }

    private static async Task<Fixture> NewFixtureAsync(
        bool enabled = true, Func<IProtectiveStopOrderStore, IProtectiveStopOrderStore>? wrapStops = null)
    {
        var host = await NewHostAsync();
        var source = new FakePositionSource();
        var clock = new FakeClock();
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var logger = new RecordingLogger<BrokerPositionSnapshotService>();
        var service = new BrokerPositionSnapshotService(
            source,
            host.Services.GetRequiredService<IWolverineRuntime>(),
            new FuncTimeProvider(() => clock.UtcNow),
            Options.Create(new PositionReconciliationOptions { Enabled = enabled }),
            logger,
            DetectorScopes(wrapStops?.Invoke(stops) ?? stops, store, () => clock.UtcNow));
        return new Fixture(host, service, source, clock, stops, store, logger);
    }

    private static async Task<(bool Published, ITrackedSession Session)> PublishOnceAsync(Fixture f)
    {
        var published = false;
        Func<IMessageContext, Task> once = async _ =>
            published = await f.Service.PublishOnceAsync(CancellationToken.None);
        var session = await f.Host.TrackActivityForTest().ExecuteAndWaitAsync(once);
        return (published, session);
    }

    private static IReadOnlyList<SoftwareStopExecuted> Unattributed(ITrackedSession session) =>
        session.Sent.MessagesOf<SoftwareStopExecuted>()
            .Where(e => e.Outcome == SoftwareStopOutcome.UnattributedPosition)
            .ToList();

    private static BrokerPositionSnapshot Long(string symbol, int qty) => new(symbol, Market.UnitedStates, qty, 340m);

    /// <summary>
    /// 稼働 PoC の配置: その口座で唯一の S1 の痕跡が「受理後に 0 約定で取り消された決済」を送って<b>完了した</b>行である。
    /// 有効（Active）な保護記録は 1 件も無い。建玉は減っていない（取消なので永久に減らない）。
    /// </summary>
    private static ProtectiveStopOrder SeedCancelledCloseRemnant(
        InMemoryProtectiveStopOrderStore stops, InMemoryExecutedOrderStore store, string symbol = "AAPL", int quantity = 10)
    {
        var id = Guid.NewGuid();
        var created = T0.AddHours(-2);
        var row = new ProtectiveStopOrder(
            id, ProtectiveStopIds.SoftwareStopId(id), string.Empty, symbol, Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, 338.51m, 1m, Attempt: 1,
            ProtectiveStopState.Completed, created, T0.AddMinutes(-10), StopLossExecutionMethod.SoftwareStop,
            TriggeredAt: T0.AddMinutes(-11), TriggeredPrice: 338m, RemainingProtected: 0);
        stops.Save(row);
        store.Save(new ExecutionRecord(
            id, $"entry-{id:N}", symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash, PositionEffect.Open,
            quantity, 340m, quantity, 340m, OrderStatus.Filled, 0m, created));
        store.Save(new ExecutionRecord(
            ProtectiveStopIds.SoftwareCloseDecisionId(id, 1), $"close-{id:N}", symbol, Market.UnitedStates, TradeSide.Sell,
            ProductType.Cash, PositionEffect.Close, quantity, 338m, 0, 0m, OrderStatus.Cancelled, 0m, T0.AddMinutes(-10)));
        return row;
    }

    // ---- T-10-887: 有効な保護記録が 1 件も無い口座でも黙らない（NB-2） ----

    [Fact]
    public async Task T_10_887_有効な保護記録が無い口座でも受理後に取り消された決済の残りを1回だけ通知する()
    {
        // T-10-887, FR-10, #880, IADR-0412 決定1: 監査 PROBE1（240 巡回で 0 件）の配置。建玉観測の 1 巡回で 1 回鳴る。
        await using var f = await NewFixtureAsync();
        var row = SeedCancelledCloseRemnant(f.Stops, f.Store);
        f.Source.Result = [Long("AAPL", 10)];

        var (published, first) = await PublishOnceAsync(f);

        published.Should().BeTrue();
        first.Sent.MessagesOf<BrokerPositionsObserved>().Should().ContainSingle("観測の発行は従来どおり");
        var emitted = Unattributed(first);
        emitted.Should().ContainSingle("有効な記録が 1 件も無くても、取り消された決済の残りは無保護の建玉である")
            .Which.Quantity.Should().Be(10);
        emitted.Single().Symbol.Should().Be("AAPL");
        emitted.Single().EntryDecisionId.Should().Be(row.EntryDecisionId);
        f.Stops.Find(row.EntryDecisionId)!.UnattributedNotifiedQuantity.Should().Be(10);

        // 同じ状態の次の巡回（10 分後）では重ねて鳴らさない。
        f.Clock.UtcNow = T0.AddMinutes(10);
        var (_, second) = await PublishOnceAsync(f);
        Unattributed(second).Should().BeEmpty("同じ株数のまま再通知の間隔（60 分）内では鳴らさない");

        // 🔴 検知は是正ではない: 記録の状態・主張は動かない。
        f.Stops.Find(row.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        f.Stops.Find(row.EntryDecisionId)!.RemainingProtected.Should().Be(0);
    }

    // ---- T-10-888: 相乗りである（建玉照会の回数が増えない）・ガードの不変条件を壊さない ----

    [Fact]
    public async Task T_10_888_相乗りで建玉照会は1巡回1回のままでガードは有効な記録が無ければ照会しない()
    {
        // T-10-888, FR-10, #880, IADR-0412 決定1: 検知のために OpenD への往復を足していない。
        await using var f = await NewFixtureAsync();
        SeedCancelledCloseRemnant(f.Stops, f.Store);
        f.Source.Result = [Long("AAPL", 10)];

        var (_, session) = await PublishOnceAsync(f);

        Unattributed(session).Should().ContainSingle();
        f.Source.Calls.Should().Be(1, "検知は観測で取ったスナップショットを使い、照会を増やさない");

        // ガードの既存の不変条件「巡回対象が無ければ建玉照会もしない」は変えていない（ガードからは検知も出ない）。
        var broker = new GuardBroker { Positions = [Long("AAPL", 10)] };
        var guard = new ProtectiveStopGuard(
            broker, broker, f.Stops, f.Store, new InMemoryOrderReservationStore(), f.Clock);
        var result = await guard.RunOnceAsync(50);

        broker.PositionQueries.Should().Be(0);
        result.Events.Should().BeEmpty();
    }

    // ---- T-10-889: 純額 0 を挟んで再発した同数の帰属不明を 60 分待たずに知らせる（NB-1。規則 11 の「増える側」） ----

    [Fact]
    public async Task T_10_889_純額0の巡回を挟んで再発した同数の帰属不明は60分待たずに通知する_常駐()
    {
        // T-10-889, FR-10, #880, IADR-0412 決定2: 監査 PROBE2a/2c（純額 0 の後も印が 10 のまま・再出現から 59 分黙る）。
        await using var f = await NewFixtureAsync();
        var row = SeedCancelledCloseRemnant(f.Stops, f.Store);

        f.Source.Result = [Long("AAPL", 10)];
        Unattributed((await PublishOnceAsync(f)).Session).Should().ContainSingle();

        // 人手で決済して建玉照会から消えた（none）。印はリセットされる。
        f.Clock.UtcNow = T0.AddMinutes(10);
        f.Source.Result = [];
        Unattributed((await PublishOnceAsync(f)).Session).Should().BeEmpty();
        f.Stops.Find(row.EntryDecisionId)!.UnattributedNotifiedQuantity.Should().BeNull("純額 0 の巡回でも印を消す");
        f.Stops.Find(row.EntryDecisionId)!.UnattributedNotifiedAt.Should().BeNull();

        // 20 分後（最初の通知から 60 分未満）に同じ 10 株が再び現れる。
        f.Clock.UtcNow = T0.AddMinutes(20);
        f.Source.Result = [Long("AAPL", 10)];
        Unattributed((await PublishOnceAsync(f)).Session)
            .Should().ContainSingle("解消したあと再発した帰属不明は、同数でも改めて知らせる")
            .Which.Quantity.Should().Be(10);
    }

    [Fact]
    public void T_10_889_純額0の呼び出しでも通知済みの印をリセットする_関数()
    {
        // T-10-889, FR-10, #880, IADR-0412 決定2: 常駐ガード（別銘柄の Active 行で巡回が走る）からの呼び出しでも同じ。
        // スナップショットに当該銘柄が現れない（別銘柄だけ）呼び出しで、通知済みの群を訪れてリセットする。
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var row = SeedCancelledCloseRemnant(stops, store);

        var events = new List<object>();
        ProtectiveStopNetting.DetectUnattributedPositions([Long("AAPL", 10)], [], stops, store, T0, events);
        events.Should().ContainSingle();

        events.Clear();
        ProtectiveStopNetting.DetectUnattributedPositions(
            [Long("MSFT", 5)], [], stops, store, T0.AddMinutes(1), events);
        events.Should().BeEmpty();
        stops.Find(row.EntryDecisionId)!.UnattributedNotifiedQuantity.Should().BeNull();

        ProtectiveStopNetting.DetectUnattributedPositions(
            [Long("AAPL", 10)], [], stops, store, T0.AddMinutes(2), events);
        events.OfType<SoftwareStopExecuted>().Should().ContainSingle(e => e.Quantity == 10 && e.Symbol == "AAPL");
    }

    [Fact]
    public void T_10_889_代表が入れ替わった後の古い印もリセットする()
    {
        // T-10-889, FR-10, #880, IADR-0412 決定2: 通知済みの行が代表でなくなった（より新しい S1 行ができた）後も、
        // 純額 0 の呼び出しで古い印を残さない（残すと通知済みの群として訪れ続ける）。
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var older = SeedCancelledCloseRemnant(stops, store);
        stops.Save(stops.Find(older.EntryDecisionId)! with
        {
            UnattributedNotifiedQuantity = 10,
            UnattributedNotifiedAt = T0.AddMinutes(-5),
        });
        var newerId = Guid.NewGuid();
        stops.Save(new ProtectiveStopOrder(
            newerId, ProtectiveStopIds.SoftwareStopId(newerId), string.Empty, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 3, 338.51m, 1m, 1, ProtectiveStopState.Completed,
            T0.AddMinutes(-30), T0.AddMinutes(-20), StopLossExecutionMethod.SoftwareStop, RemainingProtected: 0,
            UnattributedNotifiedQuantity: 7, UnattributedNotifiedAt: T0.AddMinutes(-3)));

        ProtectiveStopNetting.DetectUnattributedPositions([], [], stops, store, T0);

        stops.FindUnattributedNotified(50).Should().BeEmpty("群の通知済みの印はすべて消す");
    }

    // ---- T-10-890: unknown と none と present を区別する（規則 11 の「減る側」） ----

    [Fact]
    public async Task T_10_890_照会不能では検知せず印を残し建玉なしでは印を消す()
    {
        // T-10-890, FR-10, #880, IADR-0412 決定3: 照会不能（null）を「帰属不明なし」と読まない。
        await using var f = await NewFixtureAsync();
        var row = SeedCancelledCloseRemnant(f.Stops, f.Store);
        f.Source.Result = [Long("AAPL", 10)];
        Unattributed((await PublishOnceAsync(f)).Session).Should().ContainSingle();

        // unknown: 観測も検知もしない。印は残る（消すと次の照会で同じ状態を重ねて鳴らす）。
        f.Clock.UtcNow = T0.AddMinutes(10);
        f.Source.Result = null;
        var (published, unknown) = await PublishOnceAsync(f);
        published.Should().BeFalse();
        unknown.Sent.MessagesOf<BrokerPositionsObserved>().Should().BeEmpty();
        Unattributed(unknown).Should().BeEmpty();
        f.Stops.Find(row.EntryDecisionId)!.UnattributedNotifiedQuantity.Should().Be(10, "照会不能は建玉なしではない");

        // unknown の直後に同じ状態が照会できても重ねて鳴らさない（形 (b) なら鳴る）。
        f.Clock.UtcNow = T0.AddMinutes(20);
        f.Source.Result = [Long("AAPL", 10)];
        Unattributed((await PublishOnceAsync(f)).Session).Should().BeEmpty();

        // none（空列）: 観測は発行し、印は消える。
        f.Clock.UtcNow = T0.AddMinutes(30);
        f.Source.Result = [];
        var (_, none) = await PublishOnceAsync(f);
        none.Sent.MessagesOf<BrokerPositionsObserved>().Should().ContainSingle(m => m.Positions.Count == 0);
        Unattributed(none).Should().BeEmpty();
        f.Stops.Find(row.EntryDecisionId)!.UnattributedNotifiedQuantity.Should().BeNull();
    }

    // ---- T-10-891: ガードと常駐の両方から呼んでも 1 回 ----

    [Fact]
    public async Task T_10_891_ガードと常駐の両方が同じ状態を検知しても通知は1回()
    {
        // T-10-891, FR-10, #880, IADR-0412 決定1: 通知済みの印の門（と楽観並行）が二重通知を止める。
        await using var f = await NewFixtureAsync();
        SeedCancelledCloseRemnant(f.Stops, f.Store);

        // ガードが巡回するための Active な行（別銘柄 MSFT。主張 5 株で建玉 5 株＝帰属不明なし）。
        var msftId = Guid.NewGuid();
        f.Stops.Save(new ProtectiveStopOrder(
            msftId, ProtectiveStopIds.SoftwareStopId(msftId), string.Empty, "MSFT", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 5, 400m, 1m, 0, ProtectiveStopState.Active,
            T0.AddHours(-3), T0.AddHours(-3), StopLossExecutionMethod.SoftwareStop, RemainingProtected: 5));
        f.Store.Save(new ExecutionRecord(
            msftId, "entry-msft", "MSFT", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, PositionEffect.Open,
            5, 410m, 5, 410m, OrderStatus.Filled, 0m, T0.AddHours(-3)));

        var positions = new List<BrokerPositionSnapshot> { Long("AAPL", 10), new("MSFT", Market.UnitedStates, 5, 410m) };
        var broker = new GuardBroker { Positions = positions };
        var guard = new ProtectiveStopGuard(
            broker, broker, f.Stops, f.Store, new InMemoryOrderReservationStore(), f.Clock);
        var fromGuard = (await guard.RunOnceAsync(50)).Events.OfType<SoftwareStopExecuted>()
            .Where(e => e.Outcome == SoftwareStopOutcome.UnattributedPosition)
            .ToList();

        f.Clock.UtcNow = T0.AddMinutes(1);
        f.Source.Result = positions;
        var fromSnapshot = Unattributed((await PublishOnceAsync(f)).Session);

        fromGuard.Should().ContainSingle().Which.Symbol.Should().Be("AAPL");
        fromSnapshot.Should().BeEmpty("ガードが通知済みの同じ状態を重ねて鳴らさない");
    }

    // ---- T-10-892: 無効化された構成では何も起きない ----

    [Fact]
    public async Task T_10_892_建玉観測が無効なら一度も照会せず検知も走らない()
    {
        // T-10-892, FR-10, #880, IADR-0412 残る制約: Reconciliation:Positions:Enabled=false では常駐ごと止まる。
        await using var f = await NewFixtureAsync(enabled: false);
        var row = SeedCancelledCloseRemnant(f.Stops, f.Store);
        f.Source.Result = [Long("AAPL", 10)];

        Func<IMessageContext, Task> startAndStop = async _ =>
        {
            await f.Service.StartAsync(CancellationToken.None);
            await f.Service.StopAsync(CancellationToken.None);
        };
        var session = await f.Host.TrackActivityForTest().ExecuteAndWaitAsync(startAndStop);

        f.Source.Calls.Should().Be(0);
        Unattributed(session).Should().BeEmpty();
        f.Stops.Find(row.EntryDecisionId)!.UnattributedNotifiedQuantity.Should().BeNull();
    }

    // ---- T-10-894: 検知の失敗は観測を巻き戻さない ----

    [Fact]
    public async Task T_10_894_検知が失敗しても観測は発行され巡回は成功扱いになる()
    {
        // T-10-894, FR-10, #880, IADR-0412 決定1: 観測はリスク管理の突合の供給元であり、検知の失敗で止めない。
        await using var f = await NewFixtureAsync(wrapStops: inner => new ThrowingFindActiveStore(inner));
        SeedCancelledCloseRemnant(f.Stops, f.Store);
        f.Source.Result = [Long("AAPL", 10)];

        var (published, session) = await PublishOnceAsync(f);

        published.Should().BeTrue();
        session.Sent.MessagesOf<BrokerPositionsObserved>().Should().ContainSingle();
        Unattributed(session).Should().BeEmpty();
        f.Logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("帰属不明"));
    }
}
