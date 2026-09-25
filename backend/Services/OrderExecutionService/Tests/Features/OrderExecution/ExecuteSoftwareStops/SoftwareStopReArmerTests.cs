using Microsoft.Extensions.Logging;
using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
using OrderExecutionService.Features.OrderExecution.PollOrderFills;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 FR-10, FR-12, UC-02, ADR-0040 決定1（S1）, #833 項目1, IADR-0389:
// **受理だけで完了させた保護記録の再武装**（T-10-700..T-10-711・T-10-731）。
//
// SoftwareStopExecutor.Settle は決済の Accepted を約定と同じように扱って行を Completed にする。
// moomoo の模擬取引の注文は当日限りで、受理された決済が 0 約定のまま失効し得る——そのとき建玉は無保護で、
// 行は誰の巡回にも載らず、通知も 1 本も出ない。約定追跡が終端を**確認した**ときだけ行を戻す。
//
// 🔴 時計は注入した固定値のみ（壁時計の待ちを作らない）。
public class SoftwareStopReArmerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 20, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaxTracking = TimeSpan.FromHours(24);

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class FakeBroker : IBrokerAdapter
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        private readonly Dictionary<string, BrokerOrder?> _responses = [];

        public void Respond(string orderId, BrokerOrder? order) => _responses[orderId] = order;

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult(_responses.TryGetValue(orderId, out var order) ? order : null);

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("約定追跡は発注しない");

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) =>
            throw new NotSupportedException("約定追跡は取消しない");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IEnumerable<string> Errors => Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    // Save が必ず落ちるストア（再武装の失敗が巡回を止めないことの固定に使う）。
    private sealed class ThrowingStopStore(IProtectiveStopOrderStore inner) : IProtectiveStopOrderStore
    {
        public void Save(ProtectiveStopOrder stop) => throw new InvalidOperationException("保存に失敗（テスト）");

        public ProtectiveStopOrder? Find(Guid entryDecisionId) => inner.Find(entryDecisionId);

        public IReadOnlyList<ProtectiveStopOrder> FindActive(int batchSize) => inner.FindActive(batchSize);

        public IReadOnlyList<ProtectiveStopOrder> FindActiveSoftwareStops(
            string symbol, Market market, TradeSide entrySide) => inner.FindActiveSoftwareStops(symbol, market, entrySide);

        public IReadOnlyList<ProtectiveStopOrder> FindCompletedSoftwareStops(
            string symbol, Market market, TradeSide entrySide, int limit) =>
            inner.FindCompletedSoftwareStops(symbol, market, entrySide, limit);

        // FR-10, #880, IADR-0412 決定2: 帰属不明の通知済みの印を持つ行。
        public IReadOnlyList<ProtectiveStopOrder> FindUnattributedNotified(int limit) =>
            inner.FindUnattributedNotified(limit);
    }

    private sealed record Fixture(
        OrderFillPoller Poller,
        SoftwareStopReArmer ReArmer,
        FakeBroker Broker,
        InMemoryProtectiveStopOrderStore Stops,
        InMemoryExecutedOrderStore Store,
        FakeClock Clock,
        RecordingLogger<SoftwareStopReArmer> ReArmLog,
        RecordingLogger<OrderFillPoller> PollLog,
        UnresolvedCloseNotificationTracker Tracker);

    private static Fixture NewFixture(IProtectiveStopOrderStore? stopStoreOverride = null)
    {
        var broker = new FakeBroker();
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var clock = new FakeClock(Now);
        var reArmLog = new RecordingLogger<SoftwareStopReArmer>();
        var pollLog = new RecordingLogger<OrderFillPoller>();
        var tracker = new UnresolvedCloseNotificationTracker();
        var reArmer = new SoftwareStopReArmer(stopStoreOverride ?? stops, store, clock, reArmLog, tracker);
        return new Fixture(
            new OrderFillPoller(broker, store, clock, reArmer, pollLog),
            reArmer, broker, stops, store, clock, reArmLog, pollLog, tracker);
    }

    // 「受理で完了させられた S1 の行」＋「その決済レグの発注記録（Accepted・未約定）」を置く。
    // これが稼働 PoC（AAPL 707 株・S1・ライン 338.51）の配置そのものである。
    private static (ProtectiveStopOrder Stop, Guid CloseDecisionId) CompletedOnAcceptance(
        Fixture f, int entryFilled = 707, int closeQuantity = 707, int attempt = 1,
        ProtectiveStopState state = ProtectiveStopState.Completed, int remainingProtected = 0)
    {
        var entryId = Guid.NewGuid();
        var created = Now.AddHours(-6);
        var stop = new ProtectiveStopOrder(
            entryId, ProtectiveStopIds.SoftwareStopId(entryId), string.Empty, "AAPL", Market.UnitedStates,
            TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, entryFilled, 338.51m, 1m, attempt,
            state, created, Now.AddMinutes(-10), StopLossExecutionMethod.SoftwareStop,
            TriggeredAt: Now.AddMinutes(-10), TriggeredPrice: 338.20m, RemainingProtected: remainingProtected,
            StalledNotifiedAt: Now.AddMinutes(-9));
        f.Stops.Save(stop);

        // エントリーの発注記録（終端・約定済み）。再武装の上限はこの約定数量である。
        f.Store.Save(new ExecutionRecord(
            entryId, "entry-1", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, entryFilled, 340m, entryFilled, 340m, OrderStatus.Filled, 0m, created));

        // 決済レグ（受理・未約定のまま残っている＝約定追跡の対象）。
        var closeDecisionId = ProtectiveStopIds.SoftwareCloseDecisionId(entryId, attempt);
        f.Store.Save(new ExecutionRecord(
            closeDecisionId, "close-1", "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, closeQuantity, 338.20m, FilledQuantity: 0, AveragePrice: 0m,
            OrderStatus.Accepted, 0m, Now.AddMinutes(-10)));

        return (stop, closeDecisionId);
    }

    private static BrokerOrder CloseSnapshot(OrderStatus status, int filled) =>
        new("close-1",
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 707, 338.20m, PositionEffect.Close),
            status, filled, filled > 0 ? 338.10m : 0m, Now.AddMinutes(-10), Now);

    // T-10-700: 受理で完了させた行は、決済レグが 0 約定で失効したら Active へ戻り、元の株数を取り戻す。
    // 🔴 これが稼働 PoC で無音のまま建玉が無保護になっていた経路そのものである。
    [Fact]
    public async Task T_10_700_受理で完了させた行は決済が0約定で失効したら全量を取り戻して再武装される()
    {
        var f = NewFixture();
        var (stop, closeDecisionId) = CompletedOnAcceptance(f);
        f.Broker.Respond("close-1", CloseSnapshot(OrderStatus.Expired, 0));

        var result = await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Terminalized.Should().Be(1);
        var reArmed = f.Stops.Find(stop.EntryDecisionId)!;
        reArmed.State.Should().Be(ProtectiveStopState.Active);
        reArmed.RemainingProtected.Should().Be(707);
        // 完了行は FindActiveSoftwareStops に載らない＝誰も見ない。戻ったことを巡回の側から固定する。
        f.Stops.FindActiveSoftwareStops("AAPL", Market.UnitedStates, TradeSide.Buy)
            .Should().ContainSingle(s => s.EntryDecisionId == stop.EntryDecisionId);

        // 唯一の運用者向けシグナル（Critical）。
        result.SoftwareStopEvents.Should().ContainSingle();
        var evt = result.SoftwareStopEvents![0];
        evt.Outcome.Should().Be(SoftwareStopOutcome.CloseUnfilled);
        evt.Quantity.Should().Be(707);
        evt.CloseDecisionId.Should().Be(closeDecisionId);
        f.ReArmLog.Errors.Should().ContainSingle(m => m.Contains("再武装"));
    }

    // T-10-701: 取消・拒否も失効と同じに扱う（3 状態はいずれも「未約定残が二度と約定しない」）。
    [Theory]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Rejected)]
    public async Task T_10_701_取消と拒否も未約定残があれば同じく再武装する(OrderStatus status)
    {
        var f = NewFixture();
        var (stop, _) = CompletedOnAcceptance(f);
        f.Broker.Respond("close-1", CloseSnapshot(status, 0));

        var result = await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);

        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Active);
        f.Stops.Find(stop.EntryDecisionId)!.RemainingProtected.Should().Be(707);
        result.SoftwareStopEvents.Should().ContainSingle();
    }

    // T-10-702: 部分約定のまま終端したら**未約定残だけ**を戻す（売れた分まで戻して二重に売らない）。
    [Fact]
    public async Task T_10_702_部分約定のまま終端したら未約定残だけを戻す()
    {
        var f = NewFixture();
        var (stop, _) = CompletedOnAcceptance(f);
        f.Broker.Respond("close-1", CloseSnapshot(OrderStatus.Cancelled, 300));

        var result = await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);

        var reArmed = f.Stops.Find(stop.EntryDecisionId)!;
        reArmed.State.Should().Be(ProtectiveStopState.Active);
        // 707 株のうち 300 株は売れた。残る 407 株だけが無保護で建玉に残っている。
        reArmed.RemainingProtected.Should().Be(407);
        result.SoftwareStopEvents![0].Quantity.Should().Be(407);
    }

    // T-10-703: 全量約定（Filled）では何も起きない。
    // 🔴 IsTerminal で判定すると**ここで戻してしまい、売った建玉をもう一度売る**。
    [Fact]
    public async Task T_10_703_全量約定では再武装もイベントも起きない()
    {
        var f = NewFixture();
        var (stop, _) = CompletedOnAcceptance(f);
        f.Broker.Respond("close-1", CloseSnapshot(OrderStatus.Filled, 707));

        var result = await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Terminalized.Should().Be(1);
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        f.Stops.Find(stop.EntryDecisionId)!.RemainingProtected.Should().Be(0);
        result.SoftwareStopEvents.Should().BeNullOrEmpty();
    }

    // 🔴 T-10-703（続き）: **判定は状態で行い、数量の食い違いでは戻さない。**
    // moomoo の応答は部分列挙・順序前後で約定数量が実際より少なく返り得る（既存の「約定数は巻き戻さない」規律）。
    // Filled と言っている注文で「発注数量に足りないぶん」を戻すと、**既に売った建玉をもう一度売る**。
    // ここが IsTerminal（Filled を含む）ではなく AbandonsUnfilledRemainder でなければならない理由である。
    [Fact]
    public void T_10_703b_Filledで約定数量が発注数量に足りなくても戻さない()
    {
        var f = NewFixture();
        var (stop, closeDecisionId) = CompletedOnAcceptance(f);
        var close = f.Store.FindByDecisionId(closeDecisionId)!;

        var evt = f.ReArmer.OnCloseTerminalized(close, OrderStatus.Filled, filledQuantity: 700);

        evt.Should().BeNull();
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        f.Stops.Find(stop.EntryDecisionId)!.RemainingProtected.Should().Be(0);
    }

    // 🔴 T-10-704: 照会できない（不明）を「未約定」と取り違えない。再武装も完了もせず、記録は非終端のまま残る。
    // ただし**無音にはしない**——受理の時点で行は完了しており、黙って据え置くと誰の巡回にも載らない。
    [Fact]
    public async Task T_10_704_照会できない決済は再武装せず据え置いたうえでCriticalを出す()
    {
        var f = NewFixture();
        var (stop, closeDecisionId) = CompletedOnAcceptance(f);
        f.Broker.Respond("close-1", null); // 当日一覧に無い・アダプタが null に倒した

        var result = await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Unknown.Should().Be(1);
        result.SoftwareStopEvents.Should().BeNullOrEmpty();
        // 帳簿は 1 文字も動かない。
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
        f.Stops.Find(stop.EntryDecisionId)!.RemainingProtected.Should().Be(0);
        // 記録は非終端のまま＝次回巡回で引き直される。
        f.Store.FindByDecisionId(closeDecisionId)!.Status.Should().Be(OrderStatus.Accepted);
        f.ReArmLog.Errors.Should().ContainSingle(m => m.Contains("照会できません"));
    }

    // T-10-705: 発行するイベントは決済レグを特定できる（運用者が「どの注文が売れなかったか」を追える）。
    [Fact]
    public async Task T_10_705_発行するイベントは決済レグと到達の記録を運べる()
    {
        var f = NewFixture();
        var (stop, closeDecisionId) = CompletedOnAcceptance(f);
        f.Broker.Respond("close-1", CloseSnapshot(OrderStatus.Expired, 0));

        var result = await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);

        var evt = result.SoftwareStopEvents![0];
        evt.EntryDecisionId.Should().Be(stop.EntryDecisionId);
        evt.Symbol.Should().Be("AAPL");
        evt.Market.Should().Be(Market.UnitedStates);
        evt.StopLossPrice.Should().Be(338.51m);
        evt.TriggeredPrice.Should().Be(338.20m);
        evt.CloseDecisionId.Should().Be(closeDecisionId);
        evt.CloseOrderId.Should().Be("close-1");
        // 決済は出していない（再武装は注文を 1 株も出さない）。
        evt.CloseIntent.Should().BeNull();
    }

    // 🔴 T-10-706: 同じレグを二度観測しても、行の主張はエントリーの約定数量を超えない（売り過ぎの上限）。
    [Fact]
    public void T_10_706_同じレグを二度観測しても主張はエントリーの約定数量を超えない()
    {
        var f = NewFixture();
        var (stop, closeDecisionId) = CompletedOnAcceptance(f);
        var close = f.Store.FindByDecisionId(closeDecisionId)!;

        f.ReArmer.OnCloseTerminalized(close, OrderStatus.Expired, filledQuantity: 0).Should().NotBeNull();
        f.Stops.Find(stop.EntryDecisionId)!.RemainingProtected.Should().Be(707);

        // 二度目（本番では起きないが、起きても 1414 株を主張しない）。
        f.ReArmer.OnCloseTerminalized(close, OrderStatus.Expired, filledQuantity: 0);
        f.Stops.Find(stop.EntryDecisionId)!.RemainingProtected.Should().Be(707);
    }

    // T-10-707: S1 の決済レグでない注文の終端化では保護記録に触らない（一致しないのが通常である）。
    [Fact]
    public async Task T_10_707_S1の決済レグでない注文の終端化では保護記録に触らない()
    {
        var f = NewFixture();
        var (stop, _) = CompletedOnAcceptance(f);

        // エントリー（PositionEffect=Open）と、S0 の手仕舞いレグ（protective-close: の名前空間）。
        var otherEntry = Guid.NewGuid();
        f.Store.Save(new ExecutionRecord(
            otherEntry, "entry-2", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, 10, 340m, 0, 0m, OrderStatus.Accepted, 0m, Now.AddMinutes(-5)));
        f.Store.Save(new ExecutionRecord(
            ProtectiveStopIds.CloseDecisionId(otherEntry, 1), "s0-close", "AAPL", Market.UnitedStates,
            TradeSide.Sell, ProductType.Cash, PositionEffect.Close, 10, 340m, 0, 0m,
            OrderStatus.Accepted, 0m, Now.AddMinutes(-5)));
        f.Broker.Respond("close-1", CloseSnapshot(OrderStatus.Filled, 707));
        f.Broker.Respond("entry-2", new BrokerOrder("entry-2",
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 10, 340m),
            OrderStatus.Expired, 0, 0m, Now.AddMinutes(-5), Now));
        f.Broker.Respond("s0-close", new BrokerOrder("s0-close",
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 10, 340m, PositionEffect.Close),
            OrderStatus.Expired, 0, 0m, Now.AddMinutes(-5), Now));

        var result = await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Terminalized.Should().Be(3);
        result.SoftwareStopEvents.Should().BeNullOrEmpty();
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // T-10-708: 再武装は到達の記録を消さない（一度到達したら価格が戻っても決済する。IADR-0344 決定4）。
    // 据え置きの通知済みフラグは落とす——再武装した後も決済できないなら、改めて鳴らすべきである。
    [Fact]
    public async Task T_10_708_再武装は到達の記録を残し据え置きの通知済みを落とす()
    {
        var f = NewFixture();
        var (stop, _) = CompletedOnAcceptance(f);
        f.Broker.Respond("close-1", CloseSnapshot(OrderStatus.Expired, 0));

        await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);

        var reArmed = f.Stops.Find(stop.EntryDecisionId)!;
        reArmed.TriggeredAt.Should().Be(Now.AddMinutes(-10));
        reArmed.TriggeredPrice.Should().Be(338.20m);
        reArmed.StalledNotifiedAt.Should().BeNull();
        // 試行番号は動かさない（次の決済は SoftwareStopExecutor が Attempt+1 で採る）。
        reArmed.Attempt.Should().Be(stop.Attempt);
    }

    // T-10-709: 再武装の失敗で巡回を止めない。ただし**無音にしない**（終端化はこの 1 回しか観測できない）。
    [Fact]
    public async Task T_10_709_再武装の失敗は巡回を止めずCriticalを残す()
    {
        var inner = new InMemoryProtectiveStopOrderStore();
        var broker = new FakeBroker();
        var store = new InMemoryExecutedOrderStore();
        var clock = new FakeClock(Now);
        var pollLog = new RecordingLogger<OrderFillPoller>();
        var reArmer = new SoftwareStopReArmer(
            new ThrowingStopStore(inner), store, clock, new RecordingLogger<SoftwareStopReArmer>(),
            new UnresolvedCloseNotificationTracker());
        var f = new Fixture(
            new OrderFillPoller(broker, store, clock, reArmer, pollLog), reArmer, broker, inner, store, clock,
            new RecordingLogger<SoftwareStopReArmer>(), pollLog, new UnresolvedCloseNotificationTracker());
        var (stop, closeDecisionId) = CompletedOnAcceptance(f);
        broker.Respond("close-1", CloseSnapshot(OrderStatus.Expired, 0));

        var result = await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);

        // 巡回は完走し、記録は終端化されている（次回巡回で二度目の再武装をしない）。
        result.Terminalized.Should().Be(1);
        store.FindByDecisionId(closeDecisionId)!.Status.Should().Be(OrderStatus.Expired);
        pollLog.Errors.Should().ContainSingle(m => m.Contains("再武装に失敗"));
        inner.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // T-10-710: 不明の Critical は巡回（既定 30 秒）ごとに重ねず、間隔をおいて鳴らし直す。
    [Fact]
    public async Task T_10_710_不明のCriticalは巡回ごとに重ねず間隔をおいて鳴らし直す()
    {
        var f = NewFixture();
        CompletedOnAcceptance(f);
        f.Broker.Respond("close-1", null);

        await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);
        await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);
        f.ReArmLog.Errors.Should().HaveCount(1);

        f.Clock.UtcNow = Now + UnresolvedCloseNotificationTracker.RenotifyInterval;
        await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);
        f.ReArmLog.Errors.Should().HaveCount(2);
    }

    // T-10-711: 部分決済で Active のまま残っていた行も、その試行が未約定で終われば残りを取り戻す
    //（決済レグは行の現在の試行番号より前の番号でも引ける）。
    [Fact]
    public void T_10_711_Activeのまま残った行も過去の試行の決済レグから残りを取り戻す()
    {
        var f = NewFixture();
        // 試行 1 で 300 株を決済し（受理）、行は Active（残 407）。その後ガードが試行 2 を送って Attempt=2 になっている。
        var (stop, _) = CompletedOnAcceptance(
            f, closeQuantity: 300, attempt: 1, state: ProtectiveStopState.Active, remainingProtected: 407);
        f.Stops.Save(stop with { Attempt = 2 });
        var close = f.Store.FindByDecisionId(ProtectiveStopIds.SoftwareCloseDecisionId(stop.EntryDecisionId, 1))!;

        var evt = f.ReArmer.OnCloseTerminalized(close, OrderStatus.Expired, filledQuantity: 0);

        evt.Should().NotBeNull();
        evt!.Quantity.Should().Be(300);
        var reArmed = f.Stops.Find(stop.EntryDecisionId)!;
        reArmed.State.Should().Be(ProtectiveStopState.Active);
        reArmed.RemainingProtected.Should().Be(707);
    }

    // 🔴 T-10-731: **同じ銘柄に S1 の行が 2 本**あり、片方（B）の決済だけが未約定で失効した。
    // 戻すのは**その決済レグを出した行（B）だけ**で、もう片方（A）には 1 株も足さない。
    //
    // 稼働 PoC の配置そのもの（AAPL 715 株・ライン 330.88 と 713 株・ライン 331.67。合計 1,428 株）。
    // 持ち主の特定を「銘柄・市場・方向で最初に見つかった行」へ緩めると、A が再武装されて B が無保護のまま残り、
    // しかも A は自分のエントリーの約定数量まで主張できてしまう。
    // 候補の並び（完了行は更新が新しい順）に依存しないことを、両方の並びで固定する。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T_10_731_同じ銘柄に2本あるとき失効した決済を出した行だけを未約定残へ戻す(bool aUpdatedLater)
    {
        var f = NewFixture();
        var entryA = Guid.NewGuid();
        var entryB = Guid.NewGuid();
        var created = Now.AddHours(-6);
        var updatedA = aUpdatedLater ? Now.AddMinutes(-5) : Now.AddMinutes(-15);
        var updatedB = aUpdatedLater ? Now.AddMinutes(-15) : Now.AddMinutes(-5);

        void Place(Guid entryId, int filled, decimal line, DateTimeOffset updatedAt, string entryOrderId)
        {
            f.Stops.Save(new ProtectiveStopOrder(
                entryId, ProtectiveStopIds.SoftwareStopId(entryId), string.Empty, "AAPL", Market.UnitedStates,
                TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, filled, line, 1m, 1,
                ProtectiveStopState.Completed, created, updatedAt, StopLossExecutionMethod.SoftwareStop,
                TriggeredAt: updatedAt, TriggeredPrice: line - 0.30m, RemainingProtected: 0));
            f.Store.Save(new ExecutionRecord(
                entryId, entryOrderId, "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
                PositionEffect.Open, filled, 335m, filled, 335m, OrderStatus.Filled, 0m, created));
        }

        Place(entryA, 715, 330.88m, updatedA, "entry-A");
        Place(entryB, 713, 331.67m, updatedB, "entry-B");

        // 両方の決済レグが受理・未約定で追跡中。A のレグはまだ非終端、B のレグは 200 株だけ約定して失効した。
        var closeA = ProtectiveStopIds.SoftwareCloseDecisionId(entryA, 1);
        var closeB = ProtectiveStopIds.SoftwareCloseDecisionId(entryB, 1);
        f.Store.Save(new ExecutionRecord(
            closeA, "close-A", "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, 715, 330.58m, 0, 0m, OrderStatus.Accepted, 0m, updatedA));
        f.Store.Save(new ExecutionRecord(
            closeB, "close-B", "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, 713, 331.37m, 0, 0m, OrderStatus.Accepted, 0m, updatedB));
        f.Broker.Respond("close-A", new BrokerOrder("close-A",
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 715, 330.58m, PositionEffect.Close),
            OrderStatus.Accepted, 0, 0m, updatedA, Now));
        f.Broker.Respond("close-B", new BrokerOrder("close-B",
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 713, 331.37m, PositionEffect.Close),
            OrderStatus.Expired, 200, 331.30m, updatedB, Now));

        var result = await f.Poller.PollOnceAsync(MaxTracking, batchSize: 100);

        result.Terminalized.Should().Be(1);

        // B だけが未約定残（713 − 200 = 513 株）へ戻る。
        var b = f.Stops.Find(entryB)!;
        b.State.Should().Be(ProtectiveStopState.Active);
        b.RemainingProtected.Should().Be(513);
        var evt = result.SoftwareStopEvents.Should().ContainSingle().Which;
        evt.EntryDecisionId.Should().Be(entryB);
        evt.Quantity.Should().Be(513);
        evt.StopLossPrice.Should().Be(331.67m);
        evt.CloseDecisionId.Should().Be(closeB);

        // A には 1 株も足さない（自分の決済はまだ結果待ちである）。
        var a = f.Stops.Find(entryA)!;
        a.State.Should().Be(ProtectiveStopState.Completed);
        a.RemainingProtected.Should().Be(0);
        a.UpdatedAt.Should().Be(updatedA);
    }
}
