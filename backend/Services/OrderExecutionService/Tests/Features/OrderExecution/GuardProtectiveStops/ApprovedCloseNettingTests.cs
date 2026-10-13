using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, ADR-0040 決定1（S1）, ADR-0050, #1330, IADR-0534: 承認済みの決済（取引判断の利確・損切り等）の約定で減った保護対象を
// 「外部要因」に数えない。数量の仕組み（観測 → 2 巡回連続で確定 → 帳簿を減らす）は変えず、分類と通知だけを分ける。
//
// 窓（規則 11）: 決済を送った時刻（予約）がその減少を**最初に観測した巡回以前**（前の端）で、約定が**確定の巡回までに**
// 発注の記録へ載っている（後の端）ときだけ承認済みと数える。仕様書 20261013_1330 の表の P+ / P− / P0 をここで固定する。
//
// 事象（2026-10-12 PoC）: MSFT の利確（Sell 456 株・発注 15:19:18Z）の 61 秒後に ProtectionReduced（外部要因）が出た。
public class ApprovedCloseNettingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(30);

    private sealed class MutableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = start;
    }

    private sealed class Broker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [];

        public List<OrderIntent> MarketCloses { get; } = [];

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            throw new NotSupportedException("S1 の行に逆指値を発注してはならない");

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCloses.Add(closeIntent);
            return Task.FromResult(new BrokerOrder(
                $"s1-close-{MarketCloses.Count}", closeIntent, OrderStatus.Accepted, 0, 0m, T0, null));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Positions);
    }

    internal sealed class RecordingLogger : ILogger<ProtectiveStopGuard>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class Fixture
    {
        public MutableClock Clock { get; } = new(T0);
        public Broker Broker { get; } = new();
        public InMemoryProtectiveStopOrderStore Stops { get; } = new();
        public InMemoryExecutedOrderStore Store { get; } = new();
        public InMemoryOrderReservationStore Reservations { get; } = new();
        public RecordingLogger Logger { get; } = new();
        public ProtectiveStopGuard Guard { get; }

        public Fixture(Func<InMemoryExecutedOrderStore, IExecutedOrderStore>? wrap = null)
        {
            var store = wrap is null ? (IExecutedOrderStore)Store : wrap(Store);
            var executor = new SoftwareStopExecutor(Broker, Broker, Stops, store, Reservations, Clock);
            Guard = new ProtectiveStopGuard(
                Broker, Broker, Stops, store, Reservations, Clock, Logger, softwareStops: executor);
        }

        public ProtectiveStopOrder ArmS1(int quantity)
        {
            var id = Guid.NewGuid();
            var stop = new ProtectiveStopOrder(
                id, ProtectiveStopIds.SoftwareStopId(id), string.Empty, "MSFT", Market.UnitedStates, TradeSide.Buy,
                ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, 500m, 1m, 0, ProtectiveStopState.Active,
                T0.AddHours(-2), T0.AddHours(-2), StopLossExecutionMethod.SoftwareStop);
            Stops.Save(stop);
            Store.Save(new ExecutionRecord(
                id, $"entry-{id:N}", "MSFT", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, PositionEffect.Open,
                quantity, 520m, quantity, 520m, OrderStatus.Filled, 0m, T0.AddHours(-2),
                ApprovalOrigin: OrderApprovalOrigin.TradeDecision));
            Broker.Positions = [Long(quantity)];
            return stop;
        }

        // 承認経路の決済を送った（予約 → 記録。moomoo は Accepted・約定 0 で返す）。
        public ExecutionRecord SendApprovedClose(
            int quantity, DateTimeOffset reservedAt, OrderApprovalOrigin? origin = OrderApprovalOrigin.TradeDecision)
        {
            var decisionId = Guid.NewGuid();
            Reservations.TryReserve(decisionId, reservedAt, BrokerProvider.MoomooSimulate,
                approvalOrigin: origin, positionEffect: PositionEffect.Close).Should().BeTrue();
            var record = new ExecutionRecord(
                decisionId, $"close-{decisionId:N}", "MSFT", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                PositionEffect.Close, quantity, 541.77m, 0, 0m, OrderStatus.Accepted, 0m, reservedAt.AddSeconds(1),
                ApprovalOrigin: origin);
            Store.Save(record);
            Reservations.MarkCompleted(decisionId, record.OrderId, reservedAt.AddSeconds(1));
            return record;
        }

        // 約定追跡が記録を Filled へ直す（moomoo の終端の時刻は照会した時刻）。
        public void TrackFill(ExecutionRecord record, DateTimeOffset polledAt) =>
            Store.UpdateOutcome(record.OrderId, OrderStatus.Filled, record.Quantity, 541.77m, 0m, polledAt)
                .Should().BeTrue();

        public async Task<IReadOnlyList<SoftwareStopExecuted>> RunAt(DateTimeOffset at)
        {
            Clock.UtcNow = at;
            var result = await Guard.RunOnceAsync(50);
            return [.. result.Events.OfType<SoftwareStopExecuted>()];
        }

        public ProtectiveStopOrder Row(ProtectiveStopOrder stop) => Stops.Find(stop.EntryDecisionId)!;
    }

    private static BrokerPositionSnapshot Long(int quantity) => new("MSFT", Market.UnitedStates, quantity, 520m);

    // T-10-2610（受け入れ基準 1）: #1330 の事象そのもの。取引判断の利確 456 株の約定で減った分は「外部要因」と呼ばない。
    [Fact]
    public async Task 取引判断の利確の約定で減った保護対象は外部要因ではなく承認済みの決済として知らせる()
    {
        var f = new Fixture();
        var stop = f.ArmS1(1_000);
        var sentAt = T0.AddMinutes(19).AddSeconds(18);
        var close = f.SendApprovedClose(456, sentAt);

        // 約定 → 建玉照会が減り、約定追跡が記録を直す（最初の観測より前）。
        f.Broker.Positions = [Long(544)];
        f.TrackFill(close, sentAt.AddSeconds(12));

        var first = await f.RunAt(sentAt.AddSeconds(31));
        first.Should().BeEmpty("観測 1 回では確定しない（数量の仕組みは変えない）");
        f.Row(stop).EffectiveProtectedQuantity.Should().Be(544, "未確定のあいだも実効数量は純額に見合う");

        var second = await f.RunAt(sentAt.AddSeconds(61));

        var released = second.Should().ContainSingle().Which;
        released.Outcome.Should().Be(SoftwareStopOutcome.ApprovedCloseReleased);
        released.Quantity.Should().Be(456);
        released.CloseDecisionId.Should().Be(close.DecisionId);
        released.CloseOrderId.Should().Be(close.OrderId);
        released.CloseOrigin.Should().Be(OrderApprovalOrigin.TradeDecision);
        second.Should().NotContain(e => e.Outcome == SoftwareStopOutcome.ProtectionReduced, "外部要因ではない");

        f.Row(stop).RemainingProtected.Should().Be(544);
        f.Row(stop).ExternalReductionObservedAt.Should().BeNull("確定で窓の前の端を捨てる");
        f.Store.FindByDecisionId(close.DecisionId)!.ProtectionReleasedQuantity.Should().Be(456, "帰属させた約定に印を付ける");
    }

    // T-10-2611（受け入れ基準 2・窓 P+＝増える側）: moomoo では建玉照会が先に約定を映し、約定追跡が記録を Filled へ直すのは
    // 最初の観測の**後**になり得る（記録の時刻は照会した時刻）。前の端だけで判定する形 (b) はここで外部要因と誤る。
    [Fact]
    public async Task 約定追跡が最初の観測の後に記録を直しても承認済みの決済として知らせる()
    {
        var f = new Fixture();
        var stop = f.ArmS1(1_000);
        var sentAt = T0.AddMinutes(19).AddSeconds(18);
        var close = f.SendApprovedClose(456, sentAt);
        f.Broker.Positions = [Long(544)];

        // 最初の観測の時点では記録はまだ Accepted・約定 0。
        (await f.RunAt(sentAt.AddSeconds(31))).Should().BeEmpty();
        f.Row(stop).ExternalReductionObservedAt.Should().Be(sentAt.AddSeconds(31), "窓の前の端＝最初の観測");

        // 約定追跡が観測の後に Filled へ直す（ExecutedAt は照会した時刻＝最初の観測より後）。
        f.TrackFill(close, sentAt.AddSeconds(45));

        var confirmed = await f.RunAt(sentAt.AddSeconds(61));

        confirmed.Should().ContainSingle()
            .Which.Should().Match<SoftwareStopExecuted>(e =>
                e.Outcome == SoftwareStopOutcome.ApprovedCloseReleased && e.Quantity == 456 && e.CloseDecisionId == close.DecisionId);
        f.Row(stop).RemainingProtected.Should().Be(544);
    }

    // T-10-2612（受け入れ基準 3・窓 P−＝減る側）: 外部要因で減った分を最初に観測した**後**に送った決済の約定は、その減少の原因ではない。
    // 約定が記録へ先に載り、建玉照会にはまだ映らないまま確定しても、その確定は外部要因である（後の端だけで判定する形 (a) はここで誤る）。
    // 後から建玉照会に映った決済の分は、次の確定で承認済みとして知らせる。
    [Fact]
    public async Task 減少を観測した後に送った決済の約定はその減少の説明に使わず次の確定で承認済みとする()
    {
        var f = new Fixture();
        var stop = f.ArmS1(1_000);

        // 人手の決済 300 株（外部要因）。
        f.Broker.Positions = [Long(700)];
        var observedAt = T0.AddMinutes(19).AddSeconds(30);
        (await f.RunAt(observedAt)).Should().BeEmpty();

        // 観測の後に取引判断の決済 200 株を送り、約定が記録へ先に載る（建玉照会はまだ 700 のまま）。
        var close = f.SendApprovedClose(200, observedAt.AddSeconds(10));
        f.TrackFill(close, observedAt.AddSeconds(20));

        var first = await f.RunAt(observedAt.Add(Cycle));
        var reduced = first.Should().ContainSingle().Which;
        reduced.Outcome.Should().Be(SoftwareStopOutcome.ProtectionReduced, "観測より後に送った決済は観測した減少の原因になり得ない");
        reduced.Quantity.Should().Be(300);
        f.Row(stop).RemainingProtected.Should().Be(700);
        f.Store.FindByDecisionId(close.DecisionId)!.ProtectionReleasedQuantity.Should().Be(0, "帰属させていない");

        // 建玉照会が決済を映す → 次の観測と確定で承認済み。
        f.Broker.Positions = [Long(500)];
        (await f.RunAt(observedAt.Add(Cycle * 2))).Should().BeEmpty();
        var second = await f.RunAt(observedAt.Add(Cycle * 3));

        second.Should().ContainSingle().Which.Should().Match<SoftwareStopExecuted>(e =>
            e.Outcome == SoftwareStopOutcome.ApprovedCloseReleased && e.Quantity == 200 && e.CloseDecisionId == close.DecisionId);
        f.Row(stop).RemainingProtected.Should().Be(500);
    }

    // T-10-2613（受け入れ基準 4・P0＝吸収）: 帰属させた利確の約定を、後の人手の決済の説明に 2 回使わない。
    [Fact]
    public async Task 帰属させた利確の約定は後の外部要因の減少の説明に使わない()
    {
        var f = new Fixture();
        var stop = f.ArmS1(1_000);
        var sentAt = T0.AddMinutes(19).AddSeconds(18);
        var close = f.SendApprovedClose(456, sentAt);
        f.Broker.Positions = [Long(544)];
        f.TrackFill(close, sentAt.AddSeconds(12));
        await f.RunAt(sentAt.AddSeconds(31));
        (await f.RunAt(sentAt.AddSeconds(61))).Should().ContainSingle()
            .Which.Outcome.Should().Be(SoftwareStopOutcome.ApprovedCloseReleased);

        // 5 分後に人手で 100 株を決済した（外部要因）。
        f.Broker.Positions = [Long(444)];
        await f.RunAt(sentAt.AddMinutes(5));
        var later = await f.RunAt(sentAt.AddMinutes(5).Add(Cycle));

        later.Should().ContainSingle().Which.Should().Match<SoftwareStopExecuted>(e =>
            e.Outcome == SoftwareStopOutcome.ProtectionReduced && e.Quantity == 100);
        f.Row(stop).RemainingProtected.Should().Be(444);
    }

    // T-10-2614（受け入れ基準 5）: 1 回の確定に承認済みの決済と外部要因が混ざれば 1 件ずつ出し、合計が減少量に一致する。
    [Fact]
    public async Task 一度の確定に承認済みの決済と外部要因が混ざれば内訳を分けて合計を減少量に一致させる()
    {
        var f = new Fixture();
        var stop = f.ArmS1(1_000);
        var sentAt = T0.AddMinutes(19);
        var close = f.SendApprovedClose(456, sentAt);
        f.TrackFill(close, sentAt.AddSeconds(5));
        f.Broker.Positions = [Long(500)]; // 456（利確）＋ 44（人手）

        await f.RunAt(sentAt.AddSeconds(30));
        var confirmed = await f.RunAt(sentAt.AddSeconds(60));

        confirmed.Should().HaveCount(2);
        confirmed.Should().ContainSingle(e => e.Outcome == SoftwareStopOutcome.ApprovedCloseReleased)
            .Which.Quantity.Should().Be(456);
        confirmed.Should().ContainSingle(e => e.Outcome == SoftwareStopOutcome.ProtectionReduced)
            .Which.Quantity.Should().Be(44);
        confirmed.Sum(e => e.Quantity).Should().Be(500, "内訳の合計＝帳簿を減らした株数（二重にも過少にも数えない）");
        f.Row(stop).RemainingProtected.Should().Be(500);
    }

    // T-10-2615（受け入れ基準 6）: 🔴 統制。分類を足しても保護は過小にも過大にもならない。
    // 承認済みと分類される配置（出どころあり）と、外部要因と分類される配置（出どころなし）で、各巡回の帳簿・実効数量が一致し、
    // 確定後は純額に一致する。到達で出す S1 の決済は残りの建玉（544 株）ちょうどで、利確した分を売り直さない。
    [Theory]
    [InlineData(true)]  // 約定追跡が最初の観測の前に記録を直す
    [InlineData(false)] // 最初の観測の後に直す（P+）
    public async Task 承認済みの決済で減った保護対象は分類の有無に依らず過小にも過大にもならない(bool trackedBeforeObservation)
    {
        var withOrigin = await RunWindow(OrderApprovalOrigin.TradeDecision, trackedBeforeObservation);
        var withoutOrigin = await RunWindow(null, trackedBeforeObservation);

        withOrigin.Books.Should().Equal(withoutOrigin.Books, "分類は数量に波及しない");
        withOrigin.Books.Should().Equal(
            [(1_000, 1_000), (1_000, 544), (544, 544), (544, 544)],
            "観測で実効数量が純額へ下がり、確定で帳簿が純額へ揃う");
        withOrigin.CloseQuantity.Should().Be(544, "到達時の決済は残りの建玉を超えない（利確した分を売り直さない）");
        withoutOrigin.CloseQuantity.Should().Be(544);
        withOrigin.Released.Should().Be(456);
        withoutOrigin.Released.Should().Be(0);
    }

    private static async Task<(List<(int Book, int Effective)> Books, int CloseQuantity, int Released)> RunWindow(
        OrderApprovalOrigin? origin, bool trackedBeforeObservation)
    {
        var f = new Fixture();
        var stop = f.ArmS1(1_000);
        var books = new List<(int, int)>();
        void Snap() => books.Add((f.Row(stop).ProtectedQuantity, f.Row(stop).EffectiveProtectedQuantity));

        var sentAt = T0.AddMinutes(19).AddSeconds(18);
        await f.RunAt(sentAt.AddSeconds(1));
        Snap();

        var close = f.SendApprovedClose(456, sentAt.AddSeconds(2), origin);
        f.Broker.Positions = [Long(544)];
        if (trackedBeforeObservation)
            f.TrackFill(close, sentAt.AddSeconds(10));
        await f.RunAt(sentAt.AddSeconds(31));
        Snap();

        if (!trackedBeforeObservation)
            f.TrackFill(close, sentAt.AddSeconds(45));
        await f.RunAt(sentAt.AddSeconds(61));
        Snap();
        await f.RunAt(sentAt.AddSeconds(91));
        Snap();

        // 損切りラインへ到達した。S1 の決済の数量を見る。
        f.Stops.Save(f.Row(stop) with { TriggeredAt = sentAt.AddSeconds(95), TriggeredPrice = 499m });
        await f.RunAt(sentAt.AddSeconds(121));

        return (books, f.Broker.MarketCloses.Sum(c => c.Quantity),
            f.Store.FindByDecisionId(close.DecisionId)!.ProtectionReleasedQuantity);
    }

    // T-10-2616（受け入れ基準 7）: 判定の材料が無いときは外部要因に倒す（承認済みと誤って呼ばない）。
    [Fact]
    public async Task 出どころの無い決済は外部要因のまま知らせる()
    {
        var f = new Fixture();
        f.ArmS1(1_000);
        var sentAt = T0.AddMinutes(19);
        // 出どころの無い決済（#1222 より前の記録・保護の機構の決済レグと同じ形）。
        var close = f.SendApprovedClose(456, sentAt, origin: null);
        f.TrackFill(close, sentAt.AddSeconds(5));
        f.Broker.Positions = [Long(544)];

        await f.RunAt(sentAt.AddSeconds(30));
        var confirmed = await f.RunAt(sentAt.AddSeconds(60));

        confirmed.Should().ContainSingle().Which.Outcome.Should().Be(SoftwareStopOutcome.ProtectionReduced);
    }

    [Fact]
    public async Task 予約が見つからない決済は送った時刻が分からないので外部要因のまま知らせる()
    {
        var f = new Fixture();
        f.ArmS1(1_000);
        var sentAt = T0.AddMinutes(19);
        var decisionId = Guid.NewGuid();
        // 予約が無い（保持期間パージ等）承認済みの決済の記録だけがある。
        f.Store.Save(new ExecutionRecord(
            decisionId, "close-orphan", "MSFT", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, PositionEffect.Close,
            456, 541.77m, 456, 541.77m, OrderStatus.Filled, 0m, sentAt, ApprovalOrigin: OrderApprovalOrigin.TradeDecision));
        f.Broker.Positions = [Long(544)];

        await f.RunAt(sentAt.AddSeconds(30));
        var confirmed = await f.RunAt(sentAt.AddSeconds(60));

        confirmed.Should().ContainSingle().Which.Outcome.Should().Be(SoftwareStopOutcome.ProtectionReduced);
        f.Logger.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.Contains("予約が見つからず"));
    }

    [Fact]
    public void 予約ストアを渡さない突き合わせは確定した減少を全量外部要因とする()
    {
        var clock = new MutableClock(T0);
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var id = Guid.NewGuid();
        var stop = new ProtectiveStopOrder(
            id, ProtectiveStopIds.SoftwareStopId(id), string.Empty, "MSFT", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 1_000, 500m, 1m, 0, ProtectiveStopState.Active,
            T0.AddHours(-2), T0.AddHours(-2), StopLossExecutionMethod.SoftwareStop, RemainingProtected: 1_000);
        stops.Save(stop);
        store.Save(new ExecutionRecord(
            Guid.NewGuid(), "close-1", "MSFT", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, PositionEffect.Close,
            456, 541.77m, 456, 541.77m, OrderStatus.Filled, 0m, T0, ApprovalOrigin: OrderApprovalOrigin.TradeDecision));
        BrokerPositionSnapshot[] snapshot = [Long(544)];

        var events = new List<object>();
        for (var i = 0; i < ProtectiveStopNetting.ExternalReductionConfirmations; i++)
        {
            clock.UtcNow = T0.AddSeconds(30 * (i + 1));
            ProtectiveStopNetting.ReconcileShares(
                "MSFT", Market.UnitedStates, TradeSide.Buy, snapshot, stops.FindActive(10), stops, store, clock.UtcNow,
                observing: true, events: events);
        }

        events.OfType<SoftwareStopExecuted>().Should().ContainSingle()
            .Which.Outcome.Should().Be(SoftwareStopOutcome.ProtectionReduced, "判定の材料が無ければ従来どおり外部要因へ倒す");
    }

    // T-10-2618（受け入れ基準 9）: outcome を INF で残す（判断 ID・注文 ID・数量の前後・分類の理由）。
    [Fact]
    public async Task 確定した縮小の分類を判断IDと注文IDと数量の前後と理由つきでINFに残す()
    {
        var f = new Fixture();
        var stop = f.ArmS1(1_000);
        var sentAt = T0.AddMinutes(19);
        var close = f.SendApprovedClose(456, sentAt);
        f.TrackFill(close, sentAt.AddSeconds(5));
        f.Broker.Positions = [Long(500)];

        await f.RunAt(sentAt.AddSeconds(30));
        await f.RunAt(sentAt.AddSeconds(60));

        var lines = f.Logger.Entries
            .Where(e => e.Level == LogLevel.Information && e.Message.StartsWith("保護の突き合わせ: ", StringComparison.Ordinal))
            .Select(e => e.Message)
            .ToList();
        lines.Should().HaveCount(2);
        lines.Should().ContainSingle(l => l.Contains("outcome=ApprovedCloseReleased"))
            .Which.Should().Contain(stop.EntryDecisionId.ToString())
            .And.Contain(close.DecisionId.ToString())
            .And.Contain(close.OrderId)
            .And.Contain("主張 1000 株 → 500 株（うち 456 株）")
            .And.Contain("出どころ=TradeDecision")
            .And.Contain("理由=一部が承認済みの決済");
        lines.Should().ContainSingle(l => l.Contains("outcome=ProtectionReduced"))
            .Which.Should().Contain("主張 1000 株 → 500 株（うち 44 株）").And.Contain("残り 44 株は説明できない");
    }

    // 帰属の印の書き込みだけを失敗させる包み型（他はインメモリへそのまま渡す）。
    private sealed class MarkFailingStore(InMemoryExecutedOrderStore inner, bool throwOnMark) : IExecutedOrderStore
    {
        public int MarkCalls { get; private set; }

        public void Save(ExecutionRecord record) => inner.Save(record);

        public IReadOnlyList<ExecutionRecord> GetAll() => inner.GetAll();

        public ExecutionRecord? FindByDecisionId(Guid decisionId) => inner.FindByDecisionId(decisionId);

        public IReadOnlyList<ExecutionRecord> FindPendingSince(DateTimeOffset since, int batchSize) =>
            inner.FindPendingSince(since, batchSize);

        public IReadOnlyList<ExecutionRecord> FindPendingByOrderIds(IReadOnlyCollection<string> orderIds) =>
            inner.FindPendingByOrderIds(orderIds);

        public IReadOnlyList<ExecutionRecord> FindApprovedClosesWithUnreleasedFills(
            string symbol, Market market, TradeSide closeSide, int limit) =>
            inner.FindApprovedClosesWithUnreleasedFills(symbol, market, closeSide, limit);

        public bool MarkProtectionReleased(string orderId, int releasedQuantity)
        {
            MarkCalls++;
            return throwOnMark ? throw new InvalidOperationException("印の書き込みに失敗（試験）") : false;
        }

        public bool RenewTracking(string orderId, DateTimeOffset trackedFrom) => inner.RenewTracking(orderId, trackedFrom);

        public bool UpdateOutcome(
            string orderId, OrderStatus status, int filledQuantity, decimal averagePrice, decimal slippageRatio,
            DateTimeOffset executedAt) =>
            inner.UpdateOutcome(orderId, status, filledQuantity, averagePrice, slippageRatio, executedAt);
    }

    // T-10-2621（受け入れ基準 12・PR #1336 のレビュー）: 🔴 帰属の印を書けなかった約定は承認済みと呼ばず、以後の説明にも使わない。
    // 印を書けないまま承認済みと知らせると、次の確定が同じ約定でもう一度「承認済み」と説明し、外部要因の減少を隠す（迷ったら外部要因へ倒す）。
    // 印を帳簿より先に書くので、どちらが失敗しても倒れる先は外部要因だけになる。数量（帳簿）は分類の成否に依らない。
    [Theory]
    [InlineData(true)]  // 例外（DB の一時的な失敗など）
    [InlineData(false)] // 書けなかった（記録が無い・並行に進んでいた）
    public async Task 帰属の印を書けなかった約定は承認済みと呼ばず以後の説明にも使わない(bool throwOnMark)
    {
        MarkFailingStore? failing = null;
        var f = new Fixture(inner => failing = new MarkFailingStore(inner, throwOnMark));
        var stop = f.ArmS1(1_000);
        var sentAt = T0.AddMinutes(19).AddSeconds(18);
        var close = f.SendApprovedClose(456, sentAt);
        f.Broker.Positions = [Long(544)];
        f.TrackFill(close, sentAt.AddSeconds(12));

        await f.RunAt(sentAt.AddSeconds(31));
        var confirmed = await f.RunAt(sentAt.AddSeconds(61));

        failing!.MarkCalls.Should().Be(1);
        confirmed.Should().ContainSingle().Which.Should().Match<SoftwareStopExecuted>(e =>
            e.Outcome == SoftwareStopOutcome.ProtectionReduced && e.Quantity == 456);
        f.Row(stop).RemainingProtected.Should().Be(544, "帳簿は分類の成否に依らず減る（過小にも過大にもならない）");
        f.Logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains(close.OrderId));

        if (!throwOnMark)
            return; // 「書けなかった」は記録自体が以後の問い合わせに映る（記録が無い・印が進んでいる）。包み型では再現しない。

        // 印を残せなかった約定は、同じプロセスの以後の確定でも説明に使わない（人手の決済 100 株は外部要因）。
        f.Broker.Positions = [Long(444)];
        await f.RunAt(sentAt.AddMinutes(5));
        var later = await f.RunAt(sentAt.AddMinutes(5).Add(Cycle));

        later.Should().ContainSingle().Which.Should().Match<SoftwareStopExecuted>(e =>
            e.Outcome == SoftwareStopOutcome.ProtectionReduced && e.Quantity == 100);
        failing.MarkCalls.Should().Be(1, "外した約定に印を書きにいかない");
    }
}
